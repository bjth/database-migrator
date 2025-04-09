-- PostgreSQL: Add Price column to Products - 202504091005

ALTER TABLE "Products"
    ADD COLUMN "Price" DECIMAL(18, 2) NULL; -- Use DECIMAL for price

UPDATE "Products"
SET "Price" = 9.99
WHERE "Name" = 'Sample Product'; 