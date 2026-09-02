CREATE TABLE [dbo].[QuotaAllocations] (
    [QuotaAllocationId]  INT               IDENTITY (1, 1) NOT NULL,
    [UserId]             INT               NOT NULL,
    [PeriodYear]         INT               NOT NULL,
    [PeriodMonth]        INT               NOT NULL,
    [AllocatedTokens]    BIGINT            NULL,
    [TokensUsed]         BIGINT            NOT NULL,
    [IsHardStopped]      BIT               NOT NULL,
    [ResolvedLevelType]  INT               NOT NULL,
    [TierProductId]      NVARCHAR (64)     NOT NULL,
    [IsGatewayCapped]    BIT               NOT NULL,
    [ResetDate]          DATETIMEOFFSET (7) NULL
);
GO

ALTER TABLE [dbo].[QuotaAllocations]
    ADD CONSTRAINT [PK_QuotaAllocations] PRIMARY KEY CLUSTERED ([QuotaAllocationId] ASC);
GO

ALTER TABLE [dbo].[QuotaAllocations]
    ADD CONSTRAINT [FK_QuotaAllocations_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]) ON DELETE CASCADE;
GO

-- Covers the FK on UserId (it is the leading column), so no separate FK index is needed.
CREATE UNIQUE NONCLUSTERED INDEX [IX_QuotaAllocations_UserId_PeriodYear_PeriodMonth]
    ON [dbo].[QuotaAllocations]([UserId] ASC, [PeriodYear] ASC, [PeriodMonth] ASC);
GO

-- GET /quota/allocations filters every page on the current period; the unique index above leads
-- with UserId and cannot serve that seek. TierProductId and IsHardStopped trail it rather than
-- getting indexes of their own (#208): every read here is period-scoped first, so the period
-- columns must lead, and IsHardStopped alone (two distinct values) would never be chosen.
CREATE NONCLUSTERED INDEX [IX_QuotaAllocations_PeriodYear_PeriodMonth_TierProductId_IsHardStopped]
    ON [dbo].[QuotaAllocations]([PeriodYear] ASC, [PeriodMonth] ASC, [TierProductId] ASC, [IsHardStopped] ASC);
GO
