-- SQL Server: Add Price column to Products - 202504091005

ALTER TABLE [Products]
    ADD [Price] DECIMAL (18, 2) NULL;

GO

UPDATE [Products]
SET [Price] = 9.99
WHERE [Name] = 'Sample Product'; 