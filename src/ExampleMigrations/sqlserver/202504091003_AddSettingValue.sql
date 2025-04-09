-- SQL Server: Add Value column to Settings - 202504091003

ALTER TABLE [Settings]
ADD [Value] NVARCHAR(MAX) NULL;

GO

UPDATE [Settings]
SET [Value] = 'dark'
WHERE [Key] = 'DefaultTheme'; 