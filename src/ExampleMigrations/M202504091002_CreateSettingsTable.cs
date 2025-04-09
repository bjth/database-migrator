using FluentMigrator;

namespace ExampleMigrations
{
    // Timestamp uses 12 digits: yyyyMMddhhss
    [Migration(202504091002, "Create Settings Table")]
    public class M202504091002_CreateSettingsTable : Migration
    {
        public override void Up()
        {
            Create.Table("Settings")
                .WithColumn("Id").AsInt32().NotNullable().PrimaryKey().Identity()
                .WithColumn("Key").AsString(100).NotNullable().Unique();

            Insert.IntoTable("Settings").Row(new { Key = "DefaultTheme" });
        }

        public override void Down()
        {
            Delete.Table("Settings");
        }
    }
} 