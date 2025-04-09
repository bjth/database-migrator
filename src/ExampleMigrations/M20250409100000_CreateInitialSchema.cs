using FluentMigrator;

namespace ExampleMigrations
{
    // Timestamp uses 12 digits: yyyyMMddhhss
    [Migration(202504091000, "Create Initial Schema Objects")]
    public class M202504091000_CreateInitialSchema : Migration
    {
        public override void Up()
        {
            Create.Table("Users")
                .WithColumn("Id").AsInt32().NotNullable().PrimaryKey().Identity()
                .WithColumn("Username").AsString(100).NotNullable().Unique()
                .WithColumn("CreatedAt").AsDateTime().NotNullable().WithDefault(SystemMethods.CurrentUTCDateTime);

            Insert.IntoTable("Users").Row(new { Username = "admin" });
        }

        public override void Down()
        {
            Delete.Table("Users");
        }
    }
} 