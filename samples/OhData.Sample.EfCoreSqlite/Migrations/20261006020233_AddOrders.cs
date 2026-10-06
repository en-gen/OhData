using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OhData.Sample.EfCoreSqlite.Migrations;

/// <inheritdoc />
public partial class AddOrders : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Customers",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Customers", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Orders",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Number = table.Column<string>(type: "TEXT", nullable: false),
                CustomerId = table.Column<int>(type: "INTEGER", nullable: false),
                ShipFirst = table.Column<string>(type: "TEXT", nullable: false),
                ShipLast = table.Column<string>(type: "TEXT", nullable: false),
                TotalCents = table.Column<int>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Orders", x => x.Id);
                table.ForeignKey(
                    name: "FK_Orders_Customers_CustomerId",
                    column: x => x.CustomerId,
                    principalTable: "Customers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Orders_CustomerId",
            table: "Orders",
            column: "CustomerId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Orders");

        migrationBuilder.DropTable(
            name: "Customers");
    }
}
