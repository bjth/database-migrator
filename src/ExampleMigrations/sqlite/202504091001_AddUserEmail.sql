-- SQLite: Add Email column to Users - 202504091001

ALTER TABLE "Users"
    ADD COLUMN "Email" TEXT NULL;

UPDATE "Users"
SET "Email" = 'admin@example.com'
WHERE "Username" = 'admin'; 