CREATE TABLE [dbo].[SystemConfigurations] (
    [Key]              NVARCHAR (200)     NOT NULL,
    [Value]            NVARCHAR (4000)    NOT NULL,
    [UpdatedDate]      DATETIMEOFFSET (7) NOT NULL,
    [UpdatedByUserId]  INT                NULL
);
GO

ALTER TABLE [dbo].[SystemConfigurations]
    ADD CONSTRAINT [PK_SystemConfigurations] PRIMARY KEY CLUSTERED ([Key] ASC);
GO

ALTER TABLE [dbo].[SystemConfigurations]
    ADD CONSTRAINT [FK_SystemConfigurations_Users_UpdatedByUserId] FOREIGN KEY ([UpdatedByUserId]) REFERENCES [dbo].[Users] ([UserId]);
GO

CREATE NONCLUSTERED INDEX [IX_SystemConfigurations_UpdatedByUserId]
    ON [dbo].[SystemConfigurations]([UpdatedByUserId] ASC);
GO
