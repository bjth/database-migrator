-- SQL Script Example for PostgreSQL - 20250409100100
-- Add an email column to the Users table

-- Use quoted identifiers for case-sensitivity
ALTER TABLE "Users"
ADD COLUMN "Email" VARCHAR(255) NULL;

-- Example of updating existing data
UPDATE "Users"
SET "Email" = 'admin@example.com'
WHERE "Username" = 'admin'; 