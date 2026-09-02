CREATE TABLE [dbo].[QuotaIncreaseRequests] (
    [QuotaIncreaseRequestId]     INT               IDENTITY (1, 1) NOT NULL,
    [QuotaIncreaseRequestUnique] UNIQUEIDENTIFIER  NOT NULL,
    [UserId]                     INT               NOT NULL,
    [RequestedByUserId]          INT               NOT NULL,
    [PeriodYear]                 INT               NOT NULL,
    [PeriodMonth]                INT               NOT NULL,
    [CurrentQuota]               BIGINT            NULL,
    [RequestedQuota]             BIGINT            NULL,
    [Justification]              NVARCHAR (2000)   NOT NULL,
    [StatusType]                 INT               NOT NULL,
    [ReviewedByUserId]           INT               NULL,
    [ReviewedDate]               DATETIMEOFFSET (7) NULL,
    [ReviewNotes]                NVARCHAR (2000)   NOT NULL,
    [CreatedDate]                DATETIMEOFFSET (7) NOT NULL
);
GO

ALTER TABLE [dbo].[QuotaIncreaseRequests]
    ADD CONSTRAINT [PK_QuotaIncreaseRequests] PRIMARY KEY CLUSTERED ([QuotaIncreaseRequestId] ASC);
GO

ALTER TABLE [dbo].[QuotaIncreaseRequests]
    ADD CONSTRAINT [FK_QuotaIncreaseRequests_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]) ON DELETE CASCADE;
GO

ALTER TABLE [dbo].[QuotaIncreaseRequests]
    ADD CONSTRAINT [FK_QuotaIncreaseRequests_Users_RequestedByUserId] FOREIGN KEY ([RequestedByUserId]) REFERENCES [dbo].[Users] ([UserId]);
GO

ALTER TABLE [dbo].[QuotaIncreaseRequests]
    ADD CONSTRAINT [FK_QuotaIncreaseRequests_Users_ReviewedByUserId] FOREIGN KEY ([ReviewedByUserId]) REFERENCES [dbo].[Users] ([UserId]);
GO

CREATE UNIQUE NONCLUSTERED INDEX [IX_QuotaIncreaseRequests_QuotaIncreaseRequestUnique]
    ON [dbo].[QuotaIncreaseRequests]([QuotaIncreaseRequestUnique] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_QuotaIncreaseRequests_CreatedDate]
    ON [dbo].[QuotaIncreaseRequests]([CreatedDate] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_QuotaIncreaseRequests_UserId_StatusType]
    ON [dbo].[QuotaIncreaseRequests]([UserId] ASC, [StatusType] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_QuotaIncreaseRequests_RequestedByUserId]
    ON [dbo].[QuotaIncreaseRequests]([RequestedByUserId] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_QuotaIncreaseRequests_ReviewedByUserId]
    ON [dbo].[QuotaIncreaseRequests]([ReviewedByUserId] ASC);
GO
