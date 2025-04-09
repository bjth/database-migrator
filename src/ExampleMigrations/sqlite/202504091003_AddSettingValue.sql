-- SQLite: Add Value column to Settings - 202504091003

ALTER TABLE "Settings"
ADD COLUMN "Value" TEXT NULL;

UPDATE "Settings"
SET "Value" = 'dark'
WHERE "Key" = 'DefaultTheme'; 