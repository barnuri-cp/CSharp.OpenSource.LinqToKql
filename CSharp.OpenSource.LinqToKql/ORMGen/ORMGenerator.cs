﻿using CSharp.OpenSource.LinqToKql.Extensions;
using CSharp.OpenSource.LinqToKql.Models;
using CSharp.OpenSource.LinqToKql.Provider;

namespace CSharp.OpenSource.LinqToKql.ORMGen;

public class ORMGenerator
{
    public ORMGeneratorConfig Config { get; set; }
    protected virtual ORMKustoDbContext DbContext { get; set; }
    protected const string NewLine = "\n";
    protected const string TAB = "    ";

    public ORMGenerator(ORMGeneratorConfig config)
    {
        Config = config;
        DbContext = new(new KustoDbContextExecutor(config.ProviderExecutor));
    }

    public virtual async Task GenerateAsync()
    {
        PrepareFolders();
        Config.ModelsNamespace ??= Config.Namespace ?? throw new ArgumentNullException(nameof(Config.ModelsNamespace));
        Config.DbContextNamespace ??= Config.Namespace ?? throw new ArgumentNullException(nameof(Config.DbContextNamespace));
        Config.DbContextName ??= $"My{nameof(ORMKustoDbContext)}";
        var models = new List<ORMGenaratedModel>();
        foreach (var dbConfig in Config.DatabaseConfigs)
        {
            Console.WriteLine(" ");
            Console.WriteLine("-------------------------");
            Console.WriteLine($" Start Generate {dbConfig.DatabaseName}");
            Console.WriteLine("-------------------------");
            Console.WriteLine(" ");

            Console.WriteLine("---------- Tables ----------");
            var tables = await GetTablesAsync(dbConfig);
            foreach (var table in tables)
            {
                Console.WriteLine($"{table.Name} Start");
                models.Add(await GenerateTableModelAsync(table, dbConfig));
                Console.WriteLine($"{table.Name} End");
            }
            Console.WriteLine(" ");

            Console.WriteLine("---------- Functions ----------");
            var functions = await GetFunctionsAsync(dbConfig);
            foreach (var function in functions)
            {
                Console.WriteLine($"{function.Name} Start");
                models.Add(await GenerateFunctionModelAsync(function, dbConfig));
                Console.WriteLine($"{function.Name} End");
            }
            Console.WriteLine(" ");
        }
        if (Config.CreateDbContext)
        {
            Console.WriteLine("---------- DbContext ----------");
            await GenerateDbContextAsync(models);
        }
    }

    protected virtual async Task GenerateDbContextAsync(List<ORMGenaratedModel> models)
    {
        var usings = new List<string>
        {
            Config.Namespace,
            Config.ModelsNamespace,
            typeof(ORMKustoDbContext).Namespace!,
            typeof(LinqToKqlProvider<>).Namespace!,
            typeof(ObjectExtension).Namespace!,
        };
        var lines = new List<string>
        {
            $"public partial class {Config.DbContextName} : {nameof(ORMKustoDbContext)}",
            $"{{",

            // ctor
            $"{TAB}public {Config.DbContextName}(IKustoDbContextExecutor executor) : base(executor)",
            $"{TAB}{{",
            $"{TAB}}}",
            "",
        };

        // props
        foreach (var (model, index) in models.Select((model, index) => (model, index)))
        {
            if (index != 0) { lines.Add(""); }
            lines.Add($"{TAB}public virtual IQueryable<{model.TypeName}> {model.TableOrFunctionDeclaration}");
            lines.Add($"{TAB}{TAB}=> CreateQuery<{model.TypeName}>($\"{model.KQL}\");");
        }
        lines.Add($"}}");
        await File.WriteAllTextAsync(Config.DbContextFilePath, WrapContentWithNamespaceAndUsing(lines, usings, Config.DbContextNamespace));
    }

    protected virtual string WrapContentWithNamespaceAndUsing(List<string> lines, List<string> usings, string @namespace)
    {
        var res = new List<string>();
        res.Add($"// <auto-generated> This file has been auto generated by {typeof(ORMGenerator).FullName}. </auto-generated>");
        res.Add("#pragma warning disable IDE1006 // Naming Styles");
        if (Config.EnableNullable) { res.Add("#nullable enable"); }
        usings = usings.Where(x => !string.IsNullOrEmpty(x))
            .Distinct()
            .Where(x => x != @namespace)
            .Select(x => x.StartsWith("using") ? x : $"using {x};")
            .ToList();
        res.AddRange(usings);
        res.Add("");
        res.Add($"namespace {@namespace}{(Config.FileScopedNamespaces ? ";" : "")}");
        if (!Config.FileScopedNamespaces) { res.Add($"{{"); }
        else { res.Add(""); }
        res.AddRange(lines.Select(line => $"{(Config.FileScopedNamespaces ? "" : TAB)}{line}"));
        if (!Config.FileScopedNamespaces) { res.Add($"}}"); }
        return string.Join(NewLine, res);
    }

    protected virtual async Task<ORMGenaratedModel> GenerateFunctionModelAsync(ShowFunctionsResult function, ORMGeneratorDatabaseConfig dbConfig)
    {
        var csharpParams = string.Join(", ", function.ParametersItems.Select(x => $"{x.Type} {x.Name}"));
        var kqlParams = string.Join(", ", function.ParametersItems.Select(x => $"{{{x.Name}.GetKQLValue()}}"));

        var usings = new List<string> { "System" };
        var lines = new List<string>
        {
            $"public partial class {function.Name}",
            $"{{",
        };
        var functionColumns = await GetFunctionSchemaAsync(function, dbConfig);
        foreach (var column in functionColumns)
        {
            lines.Add($"{TAB}public virtual {DataTypeTranslate(column.DataType)} {column.ColumnName} {{ get; set; }}");
        }
        lines.Add($"}}");
        var fileContent = WrapContentWithNamespaceAndUsing(lines, usings, Config.ModelsNamespace);
        var modelFolder = dbConfig.ModelSubFolderName != null
            ? Path.Combine(Config.ModelsFolderPath, dbConfig.ModelSubFolderName)
            : Config.ModelsFolderPath;
        if (!Directory.Exists(modelFolder)) { Directory.CreateDirectory(modelFolder); }
        var filePath = Path.Combine(modelFolder, $"{function.Name}.cs");
        await File.WriteAllTextAsync(filePath, fileContent);
        return new()
        {
            TypeName = function.Name,
            KQL = $"{function.Name}({kqlParams})",
            TableOrFunctionDeclaration = $"{function.Name}({csharpParams})"
        };
    }

    private async Task<List<GetSchemaResult>> GetFunctionSchemaAsync(ShowFunctionsResult function, ORMGeneratorDatabaseConfig dbConfig)
    {
        return await DbContext.CreateQuery<GetSchemaResult>($"{function.Name}({string.Join(", ", function.ParametersItems.Select(x => DefaultValue(x.Type)))})", dbConfig.DatabaseName)
            .Take(1)
            .FromKQL("getschema")
            .ToListAsync();
    }

    protected virtual async Task<ORMGenaratedModel> GenerateTableModelAsync(ORMGeneratorTable table, ORMGeneratorDatabaseConfig dbConfig)
    {
        var usings = new List<string> { "System" };
        var lines = new List<string>
        {
            $"public partial class {table.Name}",
            $"{{",
        };
        foreach (var column in table.Columns)
        {
            lines.Add($"{TAB}public virtual {DataTypeTranslate(column.ColumnType)} {column.ColumnName} {{ get; set; }}");
        }
        lines.Add($"}}");
        var fileContent = WrapContentWithNamespaceAndUsing(lines, usings, Config.ModelsNamespace);
        var modelFolder = dbConfig.ModelSubFolderName != null
            ? Path.Combine(Config.ModelsFolderPath, dbConfig.ModelSubFolderName)
            : Config.ModelsFolderPath;
        if (!Directory.Exists(modelFolder)) { Directory.CreateDirectory(modelFolder); }
        var filePath = Path.Combine(modelFolder, $"{table.Name}.cs");
        await File.WriteAllTextAsync(filePath, fileContent);
        return new()
        {
            TypeName = table.Name,
            KQL = table.Name,
            TableOrFunctionDeclaration = table.Name,
        };
    }

    protected virtual async Task<List<ORMGeneratorTable>> GetTablesAsync(ORMGeneratorDatabaseConfig dbConfig)
    {
        var tables = await DbContext.CreateQuery<ShowSchemaResult>(".show schema", dbConfig.DatabaseName)
            .Where(x => x.DatabaseName == dbConfig.DatabaseName)
            .ToListAsync();
        var filters = Config.Filters.TableFilters
            .Concat(Config.Filters.GlobalFilters)
            .Concat(dbConfig.Filters.GlobalFilters)
            .Concat(dbConfig.Filters.TableFilters)
            .ToList();
        return ApplyFilters(tables, t => t.TableName, filters).GroupBy(x => x.TableName)
                .Select(x => new ORMGeneratorTable { Name = x.Key, Columns = x.Where(x => !string.IsNullOrEmpty(x.ColumnName)).ToList() })
                .ToList();
    }

    protected virtual async Task<List<ShowFunctionsResult>> GetFunctionsAsync(ORMGeneratorDatabaseConfig dbConfig)
    {
        var functions = await DbContext.CreateQuery<ShowFunctionsResult>(".show functions", dbConfig.DatabaseName)
            .ToListAsync();
        var filters = Config.Filters.FunctionFilters
            .Concat(Config.Filters.GlobalFilters)
            .Concat(dbConfig.Filters.GlobalFilters)
            .Concat(dbConfig.Filters.FunctionFilters)
            .ToList();
        functions = ApplyFilters(functions, t => t.Name, filters);
        foreach (var function in functions)
        {
            function.ParametersItems = function.Parameters.TrimStart('(').TrimEnd(')')
              .Split(',')
              .Where(x => !string.IsNullOrEmpty(x)) // solve empty funcs
              .Select(x => x.Split(':'))
              .Select(x => new ORMGeneratorFunctionParam
              {
                  Name = x[0],
                  Type = x[1],
              })
              .ToList();
        }
        return functions;
    }

    protected virtual string DataTypeTranslate(string kustoDataType)
    {
        var type = kustoDataType.Replace("System.", "");
        type = type switch
        {
            nameof(String) => nameof(String).ToLower(),
            nameof(Object) => nameof(Object).ToLower(),
            nameof(SByte) => nameof(SByte).ToLower(),
            _ => type,
        };
        if (!Config.EnableNullable && type == "string")
        {
            return type;
        }
        return type + "?";
    }

    protected virtual List<T> ApplyFilters<T>(List<T> list, Func<T, string> valueGetter, List<ORMGeneratorFilter> filters)
    {
        return list.FindAll(table =>
        {
            if (filters.Any(f => !f.Exclude && f.Match(valueGetter(table))))
            {
                return true;
            }
            if (filters.Any(f => f.Exclude && f.Match(valueGetter(table))))
            {
                return false;
            }
            return true;
        });
    }

    protected virtual void PrepareFolders()
    {
        if (!Directory.Exists(Config.ModelsFolderPath)) { Directory.CreateDirectory(Config.ModelsFolderPath); }
        if (!Directory.Exists(Config.DbContextFolderPath)) { Directory.CreateDirectory(Config.DbContextFolderPath); }
        if (Config.CleanFolderBeforeCreate)
        {
            if (File.Exists(Config.DbContextFilePath))
            {
                File.Delete(Config.DbContextFilePath);
            }
            foreach (var file in Directory.GetFiles(Config.ModelsFolderPath, "*", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }
        }
    }

    // https://learn.microsoft.com/en-us/kusto/query/scalar-data-types/?view=microsoft-fabric
    public string CsharpType(string kustoType) => kustoType switch
    {
        "bool" or "boolean" => "bool?",
        "datetime" or "date" => $"{nameof(DateTime)}?",
        "decimal" => "decimal?",
        "guid" or "uudi" or "uniqueid" => $"{nameof(Guid)}?",
        "int" => "int?",
        "long" => "long?",
        "real" => "double?",
        "double" => "double?",
        "string" => $"string{(Config.EnableNullable ? "?" : "")}",
        "timespan" or "time" => $"{nameof(TimeSpan)}?",
        "dynamic" => "object?",
        _ => "object?",
    };

    public string DefaultValue(string kustoType) => kustoType switch
    {
        "bool" or "boolean" => false.GetKQLValue(),
        "datetime" or "date" => DateTime.UtcNow.GetKQLValue(),
        "decimal" or "int" or "long" or "double" or "real" => (-1).GetKQLValue(),
        "guid" or "uudi" or "uniqueid" => Guid.Empty.GetKQLValue(),
        "string" => "''",
        "timespan" or "time" => TimeSpan.FromSeconds(1).GetKQLValue(),
        "dynamic" => "dynamic({})",
        _ => "null",
    };
}
