-- PostgreSQL: Add Email column to Users - 202504091001

ALTER TABLE "Users"
    ADD COLUMN "Email" VARCHAR(255) NULL; -- VARCHAR is appropriate

UPDATE "Users"
SET "Email" = 'admin@example.com'
WHERE "Username" = 'admin'; 