using FluentMigrator;

namespace ExampleMigrations
{
    // Timestamp uses 12 digits: yyyyMMddhhss
    [Migration(202504091004, "Create Products Table")]
    public class M202504091004_CreateProductsTable : Migration
    {
        public override void Up()
        {
            Create.Table("Products")
                .WithColumn("Id").AsInt32().NotNullable().PrimaryKey().Identity()
                .WithColumn("Name").AsString(200).NotNullable();

            Insert.IntoTable("Products").Row(new { Name = "Sample Product" });
        }

        public override void Down()
        {
            Delete.Table("Products");
        }
    }
} 