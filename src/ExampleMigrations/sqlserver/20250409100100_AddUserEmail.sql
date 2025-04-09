-- SQL Script Example for SQL Server - 20250409100100
-- Add an email column to the Users table

ALTER TABLE [Users] -- Use square brackets for SQL Server identifiers
ADD [Email] VARCHAR(255) NULL; -- Use ADD without COLUMN for SQL Server

-- Add GO separator for batch execution
GO 

-- Example of updating existing data
UPDATE [Users]
SET [Email] = 'admin@example.com'
WHERE [Username] = 'admin'; 