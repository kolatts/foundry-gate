CREATE TABLE [dbo].[AuditLogs] (
    [AuditLogId]    INT               IDENTITY (1, 1) NOT NULL,
    [ActorUserId]   INT               NULL,
    [Action]        NVARCHAR (100)    NOT NULL,
    [TargetType]    NVARCHAR (50)     NOT NULL,
    [TargetId]      NVARCHAR (100)    NOT NULL,
    [Details]       NVARCHAR (MAX)    NOT NULL,
    [OccurredDate]  DATETIMEOFFSET (7) NOT NULL
);
GO

ALTER TABLE [dbo].[AuditLogs]
    ADD CONSTRAINT [PK_AuditLogs] PRIMARY KEY CLUSTERED ([AuditLogId] ASC);
GO

ALTER TABLE [dbo].[AuditLogs]
    ADD CONSTRAINT [FK_AuditLogs_Users_ActorUserId] FOREIGN KEY ([ActorUserId]) REFERENCES [dbo].[Users] ([UserId]);
GO

CREATE NONCLUSTERED INDEX [IX_AuditLogs_ActorUserId]
    ON [dbo].[AuditLogs]([ActorUserId] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_AuditLogs_OccurredDate]
    ON [dbo].[AuditLogs]([OccurredDate] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_AuditLogs_Action_TargetType_TargetId_OccurredDate]
    ON [dbo].[AuditLogs]([Action] ASC, [TargetType] ASC, [TargetId] ASC, [OccurredDate] ASC);
GO
