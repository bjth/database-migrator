-- PostgreSQL: Add Value column to Settings - 202504091003

ALTER TABLE "Settings"
    ADD COLUMN "Value" TEXT NULL; -- TEXT is fine for PostgreSQL too

UPDATE "Settings"
SET "Value" = 'DefaultValue'
WHERE "Key" = 'DefaultTheme';
