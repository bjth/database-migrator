-- SQLite: Add Price column to Products - 202504091005

ALTER TABLE "Products"
    ADD COLUMN "Price" REAL NULL;

UPDATE "Products"
SET "Price" = 9.99
WHERE "Name" = 'Sample Product'; 