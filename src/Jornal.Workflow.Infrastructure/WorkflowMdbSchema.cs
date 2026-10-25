namespace Jornal.Workflow.Infrastructure;

public enum WorkflowMdbType { Guid, Text, Memo, Decimal, Int64, Int32, Boolean, Utc, Date, UInt32 }
public sealed record WorkflowMdbColumn(string Name, string? Path, WorkflowMdbType Type, bool Required = true, int Size = 255)
{
    public string SqlType => Type switch
    {
        WorkflowMdbType.Guid => "GUID",
        WorkflowMdbType.Text => $"TEXT({Size})",
        WorkflowMdbType.Memo => "LONGTEXT",
        WorkflowMdbType.Decimal => "DECIMAL(28,10)",
        WorkflowMdbType.Int64 => "DECIMAL(19,0)",
        WorkflowMdbType.UInt32 => "DECIMAL(10,0)",
        WorkflowMdbType.Int32 => "LONG",
        WorkflowMdbType.Boolean => "BIT",
        _ => "DATETIME"
    };
}
public sealed record WorkflowMdbTable(string Name, WorkflowMdbColumn[] Columns);
public sealed record WorkflowMdbIndex(string Name, string Table, string[] Columns, bool Unique = false);
public sealed record WorkflowMdbForeignKey(string Name, string Table, string[] Columns, string Parent, string[] ParentColumns);

public static class WorkflowMdbSchema
{
    public const string ProductId = "DenisPapusha.JournalWorkflow";
    public const int Version = 1;
    private static WorkflowMdbColumn C(string name, string? path, WorkflowMdbType type, bool required = true, int size = 255) => new(name, path, type, required, size);
    private static WorkflowMdbColumn Id() => C("id", "Id", WorkflowMdbType.Guid);
    private static WorkflowMdbColumn Owner(string name) => C(name, null, WorkflowMdbType.Guid);
    public static readonly WorkflowMdbColumn[] Metadata =
    [
        C("singleton", null, WorkflowMdbType.Int32), C("product_id", null, WorkflowMdbType.Text, size: 64),
        C("schema_version", null, WorkflowMdbType.Int32), C("database_id", null, WorkflowMdbType.Guid),
        C("revision", null, WorkflowMdbType.Int64), C("catalog_revision", null, WorkflowMdbType.Int64),
        C("project_root", null, WorkflowMdbType.Memo, false), C("price_source", null, WorkflowMdbType.Memo, false),
        C("price_loaded_at", null, WorkflowMdbType.Utc, false)
    ];
    public static readonly WorkflowMdbTable[] Tables =
    [
        new("employees", [Id(), C("display_name","Name",WorkflowMdbType.Text), C("color_argb","ColorArgb",WorkflowMdbType.UInt32,false),
            C("is_technologist","IsTechnologist",WorkflowMdbType.Boolean), C("is_archived","IsTechnologistArchived",WorkflowMdbType.Boolean)]),
        new("customers", [Id(), C("display_name","Name",WorkflowMdbType.Text)]),
        new("machines", [Id(), C("display_name","Name",WorkflowMdbType.Text), C("import_profile","ImportProfile",WorkflowMdbType.Text,false,64),
            C("legacy_key","LegacyKey",WorkflowMdbType.Text,false), C("legacy_name","LegacyName",WorkflowMdbType.Text,false),
            C("project_extension","ProjectExtension",WorkflowMdbType.Text,false,12)]),
        new("cutting_rates", [Id(), C("machine_id","MachineId",WorkflowMdbType.Guid), C("machine_name","Machine",WorkflowMdbType.Text),
            C("material_group","MaterialGroup",WorkflowMdbType.Text,size:80), C("thickness_mm","ThicknessMm",WorkflowMdbType.Decimal),
            C("price_per_meter","PricePerMeter",WorkflowMdbType.Decimal), C("price_per_pierce","PricePerPierce",WorkflowMdbType.Decimal,false)]),
        new("price_import_keys", [Id(), C("machine_name","Machine",WorkflowMdbType.Text), C("material_group","MaterialGroup",WorkflowMdbType.Text,size:80), C("thickness_mm","ThicknessMm",WorkflowMdbType.Decimal)]),
        new("orders", [Id(), C("order_number","Number",WorkflowMdbType.Int64), C("reference_number","ReferenceNumber",WorkflowMdbType.Text,size:40),
            C("customer_id","CustomerId",WorkflowMdbType.Guid), C("manager_id","ManagerId",WorkflowMdbType.Guid), C("technologist_id","TechnologistId",WorkflowMdbType.Guid),
            C("blcb","Blcb",WorkflowMdbType.Text,false), C("row_version","Version",WorkflowMdbType.Int64),
            C("created_at","CreatedAt",WorkflowMdbType.Utc), C("updated_at","UpdatedAt",WorkflowMdbType.Utc),
            C("created_by","CreatedById",WorkflowMdbType.Guid,false), C("deleted_at","DeletedAt",WorkflowMdbType.Utc,false), C("deleted_by","DeletedBy",WorkflowMdbType.Guid,false)]),
        new("order_items", [Id(), Owner("order_id"), C("item_number","Number",WorkflowMdbType.Int32),
            C("reviewed_variant","ReviewedThroughVariant",WorkflowMdbType.Int32), C("is_included","IsIncluded",WorkflowMdbType.Boolean)]),
        new("cutting_projects", [Id(), Owner("order_id"), C("item_id","ItemId",WorkflowMdbType.Guid), C("variant_number","Variant",WorkflowMdbType.Int32),
            C("machine_id","MachineId",WorkflowMdbType.Guid), C("thickness_mm","ThicknessMm",WorkflowMdbType.Decimal), C("project_name","Name",WorkflowMdbType.Text),
            C("native_path","NativePath",WorkflowMdbType.Memo,false), C("import_profile","ImportProfile",WorkflowMdbType.Text,false,64),
            C("created_at","CreatedAt",WorkflowMdbType.Utc), C("deleted_at","DeletedAt",WorkflowMdbType.Utc,false), C("deleted_by","DeletedBy",WorkflowMdbType.Guid,false)]),
        new("order_item_selections", [Id(), Owner("order_id"), C("project_id","CurrentProjectId",WorkflowMdbType.Guid)]),
        new("specifications", SpecificationColumns()),
        new("accepted_reports", ReportColumns()),
        new("project_renames", [Id(), Owner("project_id"), C("machine_id","MachineId",WorkflowMdbType.Guid), C("thickness_mm","ThicknessMm",WorkflowMdbType.Decimal),
            C("project_name","Name",WorkflowMdbType.Text), C("native_path","NativePath",WorkflowMdbType.Memo), C("import_profile","ImportProfile",WorkflowMdbType.Text,false,64),
            C("actor_id","ActorId",WorkflowMdbType.Guid), C("started_at","StartedAt",WorkflowMdbType.Utc)])
    ];
    private static WorkflowMdbColumn[] SpecificationColumns()
    {
        WorkflowMdbColumn P(string name, string path, WorkflowMdbType type, bool required = true, int size = 255) => C(name, "Current.Parameters." + path, type, required, size);
        WorkflowMdbColumn Q(string name, string path, WorkflowMdbType type) => C(name, "Current.Calculation." + path, type);
        return [Id(), Owner("order_id"), C("document_number","Current.DocumentNumber",WorkflowMdbType.Text), C("document_date","Current.DocumentDate",WorkflowMdbType.Date),
            C("saved_at","Current.SavedAt",WorkflowMdbType.Utc), C("author_id","Current.AuthorId",WorkflowMdbType.Guid),
            C("source_path","Current.SourcePath",WorkflowMdbType.Memo,false), C("import_profile","Current.ImportProfile",WorkflowMdbType.Text,false,64),
            P("machine_id","MachineId",WorkflowMdbType.Guid), P("machine_name","Machine",WorkflowMdbType.Text), P("material","Material",WorkflowMdbType.Text),
            P("material_group","MaterialGroup",WorkflowMdbType.Text,size:80), P("thickness_mm","ThicknessMm",WorkflowMdbType.Decimal),
            P("blank_size","BlankSize",WorkflowMdbType.Memo), P("blank_quantity","BlankQuantity",WorkflowMdbType.Decimal), P("cut_length_m","CutLengthMeters",WorkflowMdbType.Decimal),
            P("pierce_count","PierceCount",WorkflowMdbType.Int32,false), P("weight_kg","WeightKg",WorkflowMdbType.Decimal,false), P("duration_seconds","DurationSeconds",WorkflowMdbType.Int64,false),
            P("manual_coefficient","ManualCoefficient",WorkflowMdbType.Decimal), P("has_bending","HasBending",WorkflowMdbType.Boolean), P("has_chamfer","HasChamfer",WorkflowMdbType.Boolean),
            P("comment","Comment",WorkflowMdbType.Text,false), P("material_needs_review","MaterialNeedsReview",WorkflowMdbType.Boolean),
            Q("rate_id","RateId",WorkflowMdbType.Guid), Q("price_per_meter","PricePerMeter",WorkflowMdbType.Decimal), Q("price_per_pierce","PricePerPierce",WorkflowMdbType.Decimal),
            Q("length_cost","LengthCost",WorkflowMdbType.Decimal), Q("pierce_cost","PierceCost",WorkflowMdbType.Decimal), Q("total_cost","TotalCost",WorkflowMdbType.Decimal),
            Q("formula_version","FormulaVersion",WorkflowMdbType.Text)];
    }
    private static WorkflowMdbColumn[] ReportColumns()
    {
        var result = new List<WorkflowMdbColumn> { Id(), Owner("specification_id"), C("sha256","Sha256",WorkflowMdbType.Text,size:64), C("file_length","Length",WorkflowMdbType.Int64),
            C("file_name","FileName",WorkflowMdbType.Text), C("archive_path","ArchivePath",WorkflowMdbType.Memo) };
        foreach (var (name, path) in new[] {("document_number","DocumentNumber"),("document_date","DocumentDate"),("material","Material"),("material_group","MaterialGroup"),
            ("thickness","Thickness"),("blank_size","BlankSize"),("quantity","Quantity"),("cut_length","Length"),("pierces","Pierces"),("weight","Weight"),("duration","Duration")})
            result.Add(C(name, "Parsed." + path, WorkflowMdbType.Memo));
        return result.ToArray();
    }
    public static readonly WorkflowMdbIndex[] Indexes =
    [
        new("ux_order_reference","orders",["reference_number"],true), new("ux_order_number","orders",["order_number"],true),
        new("ix_order_period","orders",["created_at","id"]), new("ix_order_updated","orders",["updated_at","order_number"]), new("ix_order_deleted","orders",["deleted_at","id"]),
        new("ix_order_customer","orders",["customer_id"]), new("ix_order_manager","orders",["manager_id"]), new("ix_order_technologist","orders",["technologist_id","created_at"]),
        new("ix_order_blcb","orders",["blcb"]), new("ix_employee_name","employees",["display_name"]), new("ix_customer_name","customers",["display_name"]),
        new("ux_machine_name","machines",["display_name"],true), new("ux_rate_key","cutting_rates",["machine_id","material_group","thickness_mm"],true),
        new("ux_item_number","order_items",["order_id","item_number"],true), new("ux_item_owner","order_items",["order_id","id"],true),
        new("ix_selection_order","order_item_selections",["order_id"]),
        new("ux_project_variant","cutting_projects",["item_id","variant_number"],true), new("ux_project_item","cutting_projects",["item_id","id"],true),
        new("ux_project_owner","cutting_projects",["order_id","id"],true), new("ix_project_machine","cutting_projects",["machine_id"]),
        new("ix_spec_order","specifications",["order_id"]), new("ix_spec_machine","specifications",["machine_id"]),
        new("ux_report_spec","accepted_reports",["specification_id"],true), new("ux_rename_project","project_renames",["project_id"],true)
    ];
    public static readonly WorkflowMdbForeignKey[] ForeignKeys = BuildForeignKeys();
    private static WorkflowMdbForeignKey[] BuildForeignKeys()
    {
        var result = new List<WorkflowMdbForeignKey>();
        void F(string table, string column, string parent) => result.Add(new("fk_" + table + "_" + column, table, [column], parent, ["id"]));
        F("cutting_rates", "machine_id", "machines"); F("orders", "customer_id", "customers");
        foreach (var field in new[] { "manager_id", "technologist_id", "created_by", "deleted_by" }) F("orders", field, "employees");
        F("order_items", "order_id", "orders"); F("cutting_projects", "machine_id", "machines"); F("cutting_projects", "deleted_by", "employees");
        result.Add(new("fk_project_item", "cutting_projects", ["order_id", "item_id"], "order_items", ["order_id", "id"]));
        result.Add(new("fk_selection_item", "order_item_selections", ["order_id", "id"], "order_items", ["order_id", "id"]));
        result.Add(new("fk_item_current", "order_item_selections", ["id", "project_id"], "cutting_projects", ["item_id", "id"]));
        result.Add(new("fk_spec_project", "specifications", ["order_id", "id"], "cutting_projects", ["order_id", "id"]));
        F("specifications", "author_id", "employees"); F("specifications", "machine_id", "machines"); F("accepted_reports", "specification_id", "specifications");
        F("project_renames", "project_id", "cutting_projects"); F("project_renames", "machine_id", "machines"); F("project_renames", "actor_id", "employees");
        return result.ToArray();
    }
    public static IEnumerable<string> CreateSql()
    {
        foreach (var t in new[] { new WorkflowMdbTable("app_metadata", Metadata) }.Concat(Tables))
            yield return $"CREATE TABLE [{t.Name}] (" + string.Join(",", t.Columns.Select(c => $"[{c.Name}] {c.SqlType}" + (c.Required ? " NOT NULL" : ""))) +
                $", CONSTRAINT [pk_{t.Name}] PRIMARY KEY ([{(t.Name == "app_metadata" ? "singleton" : "id")}]))";
        foreach (var i in Indexes) yield return $"CREATE {(i.Unique ? "UNIQUE " : "")}INDEX [{i.Name}] ON [{i.Table}] (" + string.Join(",", i.Columns.Select(c => $"[{c}]")) + ")";
        foreach (var f in ForeignKeys) yield return $"ALTER TABLE [{f.Table}] ADD CONSTRAINT [{f.Name}] FOREIGN KEY (" + string.Join(",", f.Columns.Select(c => $"[{c}]")) +
            $") REFERENCES [{f.Parent}] (" + string.Join(",", f.ParentColumns.Select(c => $"[{c}]")) + ")";
    }
}
