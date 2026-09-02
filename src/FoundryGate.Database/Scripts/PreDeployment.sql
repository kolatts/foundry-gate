/*
--------------------------------------------------------------------------------------------------
Pre-deployment script — runs inside the deployment transaction, BEFORE the schema changes.

Everything here must be idempotent and safe on a database at ANY earlier version, including a brand
new empty one: DacFx runs this script on every deploy, not only on the deploy that first needs it.
Guard each block on the object it touches actually existing.
--------------------------------------------------------------------------------------------------
*/

/*
#147 / #204 — dedupe before IX_QuotaIncreaseRequests_PendingPerUserPeriod is created.

The new filtered unique index says a user may hold at most one Pending quota increase request per
billing period. Until it existed the rule was enforced only by a read-then-write check in
QuotaRequestService, which two concurrent submissions could both pass — so a fork that has been
running may already hold exactly the duplicates the index forbids. CREATE UNIQUE INDEX would then
fail, and because the dacpac step is part of the automatic deploy chain, that failure takes the whole
deployment down rather than just the index.

Resolution: keep the NEWEST pending request per (UserId, PeriodYear, PeriodMonth) and close the rest
as Rejected — the same shape a lapsed request gets from the #159 sweep (Rejected, no reviewer, a
system note saying what happened), because no human decided these either. Newest wins because it
carries the developer's most recent justification, and it is the one they would expect to see in the
queue.

Idempotent: after it has run once there are no duplicate pending rows left, so the UPDATE matches
nothing on every later deploy. It is also a no-op on a database that never had the bug, and on a
brand new one (the table does not exist yet, so the guard skips it).
*/
IF OBJECT_ID(N'[dbo].[QuotaIncreaseRequests]', N'U') IS NOT NULL
    AND OBJECT_ID(N'[dbo].[IX_QuotaIncreaseRequests_PendingPerUserPeriod]', N'I') IS NULL
BEGIN
    DECLARE @DedupedRequestCount INT;

    WITH [PendingPerUserPeriod] AS
    (
        SELECT
            [StatusType],
            [ReviewedDate],
            [ReviewNotes],
            ROW_NUMBER() OVER (
                PARTITION BY [UserId], [PeriodYear], [PeriodMonth]
                -- Newest first: the most recent justification is the one worth keeping, and
                -- QuotaIncreaseRequestId is IDENTITY so it breaks ties deterministically even when two
                -- rows share a CreatedDate (which the race that produced them makes likely).
                ORDER BY [CreatedDate] DESC, [QuotaIncreaseRequestId] DESC) AS [RowNumber]
        FROM [dbo].[QuotaIncreaseRequests]
        WHERE [StatusType] = 0 -- QuotaRequestStatusType.Pending
    )
    UPDATE [PendingPerUserPeriod]
    SET
        [StatusType] = 2, -- QuotaRequestStatusType.Rejected
        -- No ReviewedByUserId: nobody reviewed these. Deliberately left as it was rather than set to
        -- NULL, so a row that somehow carries one keeps it for the audit trail.
        [ReviewedDate] = SYSDATETIMEOFFSET(),
        [ReviewNotes] = N'Superseded by a later request (pre-deploy dedupe)'
    WHERE [RowNumber] > 1;

    SET @DedupedRequestCount = @@ROWCOUNT;

    IF @DedupedRequestCount > 0
        PRINT N'Pre-deploy: closed ' + CAST(@DedupedRequestCount AS NVARCHAR(10))
            + N' duplicate pending quota increase request(s) so IX_QuotaIncreaseRequests_PendingPerUserPeriod can be created (#147).';
END
GO
