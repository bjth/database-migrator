-- SQL Server: Add Email column to Users - 202504091001

ALTER TABLE [Users]
ADD [Email] NVARCHAR(255) NULL;

GO

UPDATE [Users]
SET [Email] = 'admin@example.com'
WHERE [Username] = 'admin'; 