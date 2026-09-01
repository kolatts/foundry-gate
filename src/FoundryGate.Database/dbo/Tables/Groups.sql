CREATE TABLE [dbo].[Groups] (
    [GroupId]            INT               IDENTITY (1, 1) NOT NULL,
    [GroupUnique]        UNIQUEIDENTIFIER  NOT NULL,
    [Name]               NVARCHAR (200)    NOT NULL,
    [Description]        NVARCHAR (1000)   NOT NULL,
    [EntraGroupId]       NVARCHAR (64)     NOT NULL,
    [IsEntraSynced]      BIT               NOT NULL,
    [MonthlyTokenQuota]  BIGINT            NULL,
    [IsUnlimited]        BIT               NOT NULL,
    [CreatedDate]        DATETIMEOFFSET (7) NOT NULL
);
GO

ALTER TABLE [dbo].[Groups]
    ADD CONSTRAINT [PK_Groups] PRIMARY KEY CLUSTERED ([GroupId] ASC);
GO

CREATE UNIQUE NONCLUSTERED INDEX [IX_Groups_GroupUnique]
    ON [dbo].[Groups]([GroupUnique] ASC);
GO
