CREATE TABLE [dbo].[Users] (
    [UserId]              INT               IDENTITY (1, 1) NOT NULL,
    [UserUnique]          UNIQUEIDENTIFIER  NOT NULL,
    [EntraObjectId]       NVARCHAR (64)     NOT NULL,
    [EmployeeId]          NVARCHAR (64)     NULL,
    [DisplayName]         NVARCHAR (200)    NOT NULL,
    [Email]               NVARCHAR (320)    NOT NULL,
    [IsActive]            BIT               NOT NULL,
    [IsUnlimited]         BIT               NOT NULL,
    [MonthlyTokenQuota]   BIGINT            NULL,
    [ApimSubscriptionId]  NVARCHAR (500)    NOT NULL,
    [ApimSubscriptionKey] NVARCHAR (1000)   NOT NULL,
    [ApimSubscriptionKeyHint] NVARCHAR (4)  NOT NULL,
    [ApimKeyIssuedDate]   DATETIMEOFFSET (7) NULL,
    [CreatedDate]         DATETIMEOFFSET (7) NOT NULL,
    [LastSyncedDate]      DATETIMEOFFSET (7) NULL
);
GO

ALTER TABLE [dbo].[Users]
    ADD CONSTRAINT [PK_Users] PRIMARY KEY CLUSTERED ([UserId] ASC);
GO

CREATE UNIQUE NONCLUSTERED INDEX [IX_Users_EntraObjectId]
    ON [dbo].[Users]([EntraObjectId] ASC);
GO

CREATE UNIQUE NONCLUSTERED INDEX [IX_Users_UserUnique]
    ON [dbo].[Users]([UserUnique] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_Users_DisplayName]
    ON [dbo].[Users]([DisplayName] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_Users_IsActive_DisplayName]
    ON [dbo].[Users]([IsActive] ASC, [DisplayName] ASC);
GO
