CREATE TABLE [dbo].[QuotaAllocations] (
    [QuotaAllocationId]  INT               IDENTITY (1, 1) NOT NULL,
    [UserId]             INT               NOT NULL,
    [PeriodYear]         INT               NOT NULL,
    [PeriodMonth]        INT               NOT NULL,
    [AllocatedTokens]    BIGINT            NULL,
    [TokensUsed]         BIGINT            NOT NULL,
    [IsHardStopped]      BIT               NOT NULL,
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
