using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using ProiectMTP.Services;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using CsvHelper;
using CsvHelper.Configuration;
using ProiectMTP.Models;

namespace ProiectMTP.Controllers
{
    [Authorize]
    public class TableController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly IAIService _aiService;
        private readonly int _defaultRowsToGenerate;

        public TableController(IConfiguration configuration, IAIService aiService)
        {
            _configuration = configuration;
            _aiService = aiService;
            _defaultRowsToGenerate = configuration.GetValue<int>("AISettings:DefaultRowsToGenerate", 5);
        }

        /// <summary>
        /// LIST: listează toate tabelele din baza de date curentă.
        /// </summary>
        public IActionResult Index()
        {
            var tables = new List<string>();
            var connStr = _configuration.GetConnectionString("MariaDbConnection");

            using (var conn = new MySqlConnection(connStr))
            {
                conn.Open();
                using (var cmd = new MySqlCommand("SHOW TABLES;", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        tables.Add(reader.GetString(0));
                    }
                }
            }

            return View(tables);
        }

        /// <summary>
        /// DETAILS: afișează datele dintr-un tabel selectat și oferă formular pentru generarea de rânduri cu AI.
        /// </summary>
         public IActionResult Details(string tableName)
        {
            // Dacă nu avem tableName, trimitem mesaj de eroare
            if (string.IsNullOrWhiteSpace(tableName))
            {
                // În acest caz, putem totuși seta ColumnsWithTypes ca listă goală, 
                // ca view-ul să nu dea NRE
                ViewBag.ColumnsWithTypes = new List<ColumnInfo>();
                ViewBag.TableName = tableName;
                ViewBag.DefaultRowsToGenerate = _defaultRowsToGenerate;
                return View(new DataTable()); // view-ul va afișa un mesaj corespunzător
            }

            var connStr = _configuration.GetConnectionString("MariaDbConnection");
            var data = new DataTable();
            var columnsWithTypes = new List<ColumnInfo>();

            try
            {
                using (var conn = new MySqlConnection(connStr))
                {
                    conn.Open();

                    // 1) "SHOW COLUMNS FROM `tableName`" pentru a citi numele și tipul fiecărei coloane
                    using (var colCmd = new MySqlCommand($"SHOW COLUMNS FROM `{tableName}`;", conn))
                    using (var colReader = colCmd.ExecuteReader())
                    {
                        while (colReader.Read())
                        {
                            var fieldName = colReader.GetString("Field");
                            var fieldType = colReader.GetString("Type");
                            columnsWithTypes.Add(new ColumnInfo
                            {
                                Name = fieldName,
                                Type = fieldType
                            });
                        }
                    }

                    // 2) Luăm primele 10 rânduri din tabel pentru previzualizare
                    using (var dataCmd = new MySqlCommand($"SELECT * FROM `{tableName}` LIMIT 10;", conn))
                    using (var adapter = new MySqlDataAdapter(dataCmd))
                    {
                        adapter.Fill(data);
                    }
                }
            }
            catch (Exception ex)
            {
                // Dacă apare orice excepție (baza de date nu există, nu are permisiuni etc.),
                // trimitem ColumnInfo ca listă goală și DataTable gol.
                columnsWithTypes = new List<ColumnInfo>();
                data = new DataTable();
                ViewBag.Error = $"Eroare la citirea structurii sau a datelor: {ex.Message}";
            }

            // Setăm întotdeauna ViewBag.ColumnsWithTypes (chiar dacă e goală)
            ViewBag.ColumnsWithTypes = columnsWithTypes;
            ViewBag.TableName = tableName;
            ViewBag.DefaultRowsToGenerate = _defaultRowsToGenerate;

            return View(data);
        }

        /// <summary>
        /// POST: primește cererea de a genera X rânduri cu AI și execută INSERT-urile.
        /// În acest nou prompt includem și tipul fiecărei coloane.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateRows(string tableName, int rowsToGenerate)
        {
            if (string.IsNullOrWhiteSpace(tableName))
                return BadRequest("Nu a fost specificat numele tabelului.");

            if (rowsToGenerate <= 0)
                rowsToGenerate = _defaultRowsToGenerate;
           
            var connStr = _configuration.GetConnectionString("MariaDbConnection");
            var columnsWithTypes = new List<ColumnInfo>();

            using (var conn = new MySqlConnection(connStr))
            {
                conn.Open();

                using (var colCmd = new MySqlCommand($"SHOW COLUMNS FROM `{tableName}`;", conn))
                using (var colReader = colCmd.ExecuteReader())
                {
                    while (colReader.Read())
                    {
                        var fieldName = colReader.GetString("Field");
                        var fieldType = colReader.GetString("Type");
                        columnsWithTypes.Add(new ColumnInfo
                        {
                            Name = fieldName,
                            Type = fieldType
                        });
                    }
                }
            }

            if (columnsWithTypes.Count == 0)
            {
                TempData["Error"] = $"Tabelul '{tableName}' nu are coloane sau nu există.";
                return RedirectToAction(nameof(Details), new { tableName });
            }

            var colsAsString = new StringBuilder();
            for (int i = 0; i < columnsWithTypes.Count; i++)
            {
                var ci = columnsWithTypes[i];
                colsAsString.Append($"{ci.Name} {ci.Type}");
                if (i < columnsWithTypes.Count - 1)
                    colsAsString.Append(", ");
            }

            var promptSb = new StringBuilder();
            promptSb.AppendLine(
                $"Generate {rowsToGenerate} valid MariaDB INSERT statements for table '{tableName}' " +
                $"(columns: {colsAsString})."
            );
            promptSb.Append("Provide only the INSERT statements, each ending with semicolon. Do NOT include any commentary or extra text.");

            string aiOutput;
            try
            {
                aiOutput = await _aiService.GenerateAsync(promptSb.ToString());
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Eroare la generarea INSERT-urilor cu AI: {ex.Message}";
                return RedirectToAction(nameof(Details), new { tableName });
            }

            // 3) Extragem doar liniile care încep cu "INSERT" și se termină cu ";"
            var insertStatements = ExtractInsertStatements(aiOutput);

            if (insertStatements.Count == 0)
            {
                TempData["Error"] = "AI nu a generat niciun statement INSERT valid.";
                return RedirectToAction(nameof(Details), new { tableName });
            }

            // 4) Executăm fiecare INSERT în baza de date
            bool allSuccess = true;
            var errorsSb = new StringBuilder();
            using (var conn = new MySqlConnection(connStr))
            {
                conn.Open();
                foreach (var stmt in insertStatements)
                {
                    try
                    {
                        using (var cmd = new MySqlCommand(stmt, conn))
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }
                    catch (Exception ex)
                    {
                        allSuccess = false;
                        errorsSb.AppendLine($"Eroare la execuția statement-ului:\n{stmt}\nMesaj: {ex.Message}");
                    }
                }
            }

            if (!allSuccess)
                TempData["Error"] = errorsSb.ToString();
            else
                TempData["Success"] = $"Au fost generate și executate {insertStatements.Count} inserții în '{tableName}'.";

            return RedirectToAction(nameof(Details), new { tableName });
        }

        /// <summary>
        /// Extrage din textul AI doar instrucțiunile care încep cu "INSERT" și se termină cu ";".
        /// </summary>
        private List<string> ExtractInsertStatements(string aiText)
        {
            var inserts = new List<string>();
            if (string.IsNullOrWhiteSpace(aiText))
                return inserts;

            var parts = aiText.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
                    inserts.Add(trimmed + ";");
            }
            return inserts;
        }
        public IActionResult ImportCsv()
        {
            // Vom trimite ViewBag.TableNames = lista tuturor tabelelor din baza de date (pentru dropdown).
            var connStr = _configuration.GetConnectionString("MariaDbConnection");
            var tables = new List<string>();

            using (var conn = new MySqlConnection(connStr))
            {
                conn.Open();
                using (var cmd = new MySqlCommand("SHOW TABLES;", conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        tables.Add(reader.GetString(0));
                }
            }

            ViewBag.TableNames = tables;
            return View();
        }

        /// <summary>
        /// POST: ImportCsv 
        /// Primește fișierul CSV, îl parsează și face inserturi în tabela selectată.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportCsv(string tableName, IFormFile csvFile)
        {
            // Validări de bază:
            if (string.IsNullOrWhiteSpace(tableName))
            {
                TempData["Error"] = "Nu ai selectat niciun tabel.";
                return RedirectToAction(nameof(ImportCsv));
            }
            if (csvFile == null || csvFile.Length == 0)
            {
                TempData["Error"] = "Nu ai încărcat niciun fișier CSV.";
                return RedirectToAction(nameof(ImportCsv));
            }

            var connStr = _configuration.GetConnectionString("MariaDbConnection");
            // 1) citim schema tabelului (nume+tip coloane) într-o listă de ColumnInfo
            var columnsWithTypes = new List<ColumnInfo>();

            using (var conn = new MySqlConnection(connStr))
            {
                conn.Open();
                using (var colCmd = new MySqlCommand($"SHOW COLUMNS FROM `{tableName}`;", conn))
                using (var colReader = colCmd.ExecuteReader())
                {
                    while (colReader.Read())
                    {
                        columnsWithTypes.Add(new ColumnInfo
                        {
                            Name = colReader.GetString("Field"),
                            Type = colReader.GetString("Type")
                        });
                    }
                }
            }

            if (columnsWithTypes.Count == 0)
            {
                TempData["Error"] = $"Tabelul '{tableName}' nu are coloane sau nu există.";
                return RedirectToAction(nameof(ImportCsv));
            }

            // 2) Folosim CsvHelper pentru a parsa CSV-ul. Vom presupune că antetul fișierului conține numele coloanelor
            //    în aceeași formă (exact) cu Field-urile din SHOW COLUMNS. Dacă antetul nu se potrivește, vom da eroare.
            List<Dictionary<string, string>> records = new List<Dictionary<string, string>>();
            try
            {
                using (var reader = new StreamReader(csvFile.OpenReadStream(), Encoding.UTF8))
                using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    HasHeaderRecord = true,
                    TrimOptions = TrimOptions.Trim,
                    IgnoreBlankLines = true
                }))
                {
                    // Citim toate înregistrările într-un buffer de gen: Dictionary<colName, value>
                    await csv.ReadAsync();
                    csv.ReadHeader();
                    var headerRow = csv.HeaderRecord; // array de stringuri cu antetul
                    // Validăm că fiecare nume din antet apare în schema tabelului
                    var schemaNames = columnsWithTypes.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var h in headerRow)
                    {
                        if (!schemaNames.Contains(h))
                        {
                            TempData["Error"] = $"Coloana '{h}' din antetul CSV nu există în tabela '{tableName}'.";
                            return RedirectToAction(nameof(ImportCsv));
                        }
                    }

                    // Citim rândurile
                    while (await csv.ReadAsync())
                    {
                        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var h in headerRow)
                        {
                            var val = csv.GetField(h);
                            dict[h] = val;
                        }
                        records.Add(dict);
                    }
                }
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Eroare la parsarea fișierului CSV: {ex.Message}";
                return RedirectToAction(nameof(ImportCsv));
            }

            if (records.Count == 0)
            {
                TempData["Error"] = "Fișierul CSV nu conține niciun rând de date.";
                return RedirectToAction(nameof(ImportCsv));
            }

            // 3) Construim și executăm INSERT-urile în baza de date (folosind tranzacție)
            try
            {
                using (var conn = new MySqlConnection(connStr))
                {
                    conn.Open();
                    using (var transaction = await conn.BeginTransactionAsync())
                    {
                        // Vom lua antetul din primul rând (dict.Keys) pentru a crea comanda INSERT
                        var headers = records[0].Keys.ToList(); // lista de coloane de inserat
                        var columnList = string.Join(", ", headers.Select(h => $"`{h}`"));
                        var paramList = string.Join(", ", headers.Select(h => $"@{h}"));

                        var insertSql = $"INSERT INTO `{tableName}` ({columnList}) VALUES ({paramList});";

                        foreach (var rowDict in records)
                        {
                            using (var cmd = new MySqlCommand(insertSql, conn, transaction))
                            {
                                // adăugăm parametrii pentru fiecare coloană
                                foreach (var h in headers)
                                {
                                    // Dacă valoarea e vidă, trimitem DBNull
                                    object val = string.IsNullOrWhiteSpace(rowDict[h])
                                        ? (object)DBNull.Value
                                        : rowDict[h];

                                    cmd.Parameters.AddWithValue($"@{h}", val);
                                }
                                await cmd.ExecuteNonQueryAsync();
                            }
                        }

                        await transaction.CommitAsync();
                    }
                }

                TempData["Success"] = $"Au fost importate {records.Count} rânduri în tabela '{tableName}'.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Eroare la inserarea datelor: {ex.Message}";
            }

            return RedirectToAction(nameof(ImportCsv));
        }
    
    }
}
