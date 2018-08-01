namespace MvcMovie.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class UPC : DbMigration
    {
        public override void Up()
        {
            AddColumn("dbo.Movies", "UPC", c => c.String(maxLength: 12));
        }
        
        public override void Down()
        {
            DropColumn("dbo.Movies", "UPC");
        }
    }
}
