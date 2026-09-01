CREATE TABLE [dbo].[GroupMembers] (
    [GroupId]        INT               NOT NULL,
    [UserId]         INT               NOT NULL,
    [AddedDate]      DATETIMEOFFSET (7) NOT NULL,
    [AddedByUserId]  INT               NULL
);
GO

ALTER TABLE [dbo].[GroupMembers]
    ADD CONSTRAINT [PK_GroupMembers] PRIMARY KEY CLUSTERED ([GroupId] ASC, [UserId] ASC);
GO

ALTER TABLE [dbo].[GroupMembers]
    ADD CONSTRAINT [FK_GroupMembers_Groups_GroupId] FOREIGN KEY ([GroupId]) REFERENCES [dbo].[Groups] ([GroupId]) ON DELETE CASCADE;
GO

ALTER TABLE [dbo].[GroupMembers]
    ADD CONSTRAINT [FK_GroupMembers_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [dbo].[Users] ([UserId]);
GO

ALTER TABLE [dbo].[GroupMembers]
    ADD CONSTRAINT [FK_GroupMembers_Users_AddedByUserId] FOREIGN KEY ([AddedByUserId]) REFERENCES [dbo].[Users] ([UserId]);
GO

-- GroupId is the leading column of the clustered PK above, so it does not need its own
-- index; UserId and AddedByUserId are not covered by that prefix and need theirs.
CREATE NONCLUSTERED INDEX [IX_GroupMembers_UserId]
    ON [dbo].[GroupMembers]([UserId] ASC);
GO

CREATE NONCLUSTERED INDEX [IX_GroupMembers_AddedByUserId]
    ON [dbo].[GroupMembers]([AddedByUserId] ASC);
GO
