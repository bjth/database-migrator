-- SQL Script Example for SQLite - 20250409100100
-- Add an email column to the Users table

ALTER TABLE "Users"
ADD COLUMN "Email" TEXT NULL; -- SQLite uses TEXT for strings

-- Example of updating existing data
UPDATE "Users"
SET "Email" = 'admin@example.com'
WHERE "Username" = 'admin'; 