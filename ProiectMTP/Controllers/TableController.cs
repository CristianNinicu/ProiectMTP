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
        public async Task<IActionResult> ImportCsv(string importMode, string tableName, string newTableName, IFormFile csvFile)
        {
            // importMode: "existing" sau "new"
            // tableName: numele tabelului existent (dacă importMode == "existing")
            // newTableName: numele tabelului nou (dacă importMode == "new")
            // csvFile: fișierul .csv încărcat

            // 1. Validează alegerea modului:
            bool isNewTable = string.Equals(importMode, "new", StringComparison.OrdinalIgnoreCase);

            if (isNewTable)
            {
                // Trebuie să avem un nume valid pentru noul tabel
                if (string.IsNullOrWhiteSpace(newTableName))
                {
                    TempData["Error"] = "Trebuie să specifici un nume pentru noul tabel.";
                    return RedirectToAction(nameof(ImportCsv));
                }
                // Milităm să nu existe deja un tabel cu același nume în baza de date
                var connStrCheck = _configuration.GetConnectionString("MariaDbConnection");
                using (var connCheck = new MySqlConnection(connStrCheck))
                {
                    connCheck.Open();
                    using (var cmdCheck = new MySqlCommand("SHOW TABLES LIKE @t;", connCheck))
                    {
                        cmdCheck.Parameters.AddWithValue("@t", newTableName);
                        var exists = cmdCheck.ExecuteScalar();
                        if (exists != null)
                        {
                            TempData["Error"] = $"Există deja o tabelă numită '{newTableName}'. Alege un alt nume.";
                            return RedirectToAction(nameof(ImportCsv));
                        }
                    }
                }

                // Vom folosi newTableName pe post de tableName de aici încolo
                tableName = newTableName;
            }
            else
            {
                // Dacă e modul existent, trebuie să avem tableName ne-gol
                if (string.IsNullOrWhiteSpace(tableName))
                {
                    TempData["Error"] = "Nu ai selectat niciun tabel existent.";
                    return RedirectToAction(nameof(ImportCsv));
                }
            }

            // 2. Fișierul CSV validations
            if (csvFile == null || csvFile.Length == 0)
            {
                TempData["Error"] = "Nu ai încărcat niciun fișier CSV.";
                return RedirectToAction(nameof(ImportCsv));
            }

            // 3. Parsăm CSV cu CsvHelper într-un buffer temporar
            List<Dictionary<string, string>> records = new List<Dictionary<string, string>>();
            string[] headerRow;
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
                    await csv.ReadAsync();
                    csv.ReadHeader();
                    headerRow = csv.HeaderRecord; // array cu numele coloanelor din CSV

                    // 3.a) Dacă modul este "new", creăm tabelul în baza de date acum, folosind headerRow
                    if (isNewTable)
                    {
                        // Construim comanda CREATE TABLE
                        // Vom pune toate coloanele ca VARCHAR(255). Poți schimba logica dacă vrei TEXT sau alte tipuri.
                        var createSb = new StringBuilder();
                        createSb.AppendLine($"CREATE TABLE `{tableName}` (");
                        createSb.AppendLine("  `id` INT(11) NOT NULL AUTO_INCREMENT,"); // cheie primară
                        for (int i = 0; i < headerRow.Length; i++)
                        {
                            var col = headerRow[i].Trim();
                            // Sanitizare minimă: nu permitem spații, slash-uri, etc
                            // Într-o versiune product-ready ai face o validare mai avansată
                            if (string.IsNullOrWhiteSpace(col) || col.Any(c => char.IsWhiteSpace(c)))
                            {
                                throw new Exception($"Nume coloană invalid în antet: '{col}'");
                            }
                            // Adaugă coloana
                            createSb.Append($"  `{col}` VARCHAR(255) NULL");
                            if (i < headerRow.Length - 1)
                                createSb.AppendLine(",");
                            else
                                createSb.AppendLine();
                        }
                        createSb.AppendLine("  , PRIMARY KEY (`id`)");
                        createSb.AppendLine(") ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;");

                        // Executăm CREATE TABLE
                        var connStrCreate = _configuration.GetConnectionString("MariaDbConnection");
                        using (var connCreate = new MySqlConnection(connStrCreate))
                        {
                            await connCreate.OpenAsync();
                            using (var cmdCreate = new MySqlCommand(createSb.ToString(), connCreate))
                            {
                                await cmdCreate.ExecuteNonQueryAsync();
                            }
                        }
                        // Tabelul a fost creat. Continuăm importul în el.
                    }

                    // 3.b) Indiferent de modul (new sau existing), citim rândurile CSV în memoria aplicației
                    while (await csv.ReadAsync())
                    {
                        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var h in headerRow)
                        {
                            var val = csv.GetField(h);
                            dict[h] = val ?? string.Empty;
                        }
                        records.Add(dict);
                    }
                }
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Eroare la parsarea fișierului CSV sau la crearea tabelului: {ex.Message}";
                return RedirectToAction(nameof(ImportCsv));
            }

            if (records.Count == 0)
            {
                TempData["Error"] = "Fișierul CSV nu conține niciun rând de date.";
                return RedirectToAction(nameof(ImportCsv));
            }

            // 4. Pregătim INSERT-urile într-o tranzacție
            try
            {
                var connStr = _configuration.GetConnectionString("MariaDbConnection");
                using (var conn = new MySqlConnection(connStr))
                {
                    await conn.OpenAsync();
                    using (var transaction = await conn.BeginTransactionAsync())
                    {
                        // Folosim headerRow ca listă de coloane pentru INSERT
                        var columnList = string.Join(", ", headerRow.Select(h => $"`{h}`"));
                        var paramList = string.Join(", ", headerRow.Select(h => $"@{h}"));
                        var insertSql = $"INSERT INTO `{tableName}` ({columnList}) VALUES ({paramList});";

                        foreach (var rowDict in records)
                        {
                            using (var cmd = new MySqlCommand(insertSql, conn, transaction))
                            {
                                foreach (var h in headerRow)
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
        
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PreviewInsert(string tableName, int rowsToGenerate = 0)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                TempData["Error"] = "Nu ai specificat niciun tabel.";
                return RedirectToAction("Details", new { tableName });
            }

            if (rowsToGenerate <= 0)
            {
                rowsToGenerate = _defaultRowsToGenerate;
            }

            // 1) Construim o listă de coloane preluată cu SHOW COLUMNS
            var columnNames = new List<string>();
            var connStr = _configuration.GetConnectionString("MariaDbConnection");

            try
            {
                using (var conn = new MySqlConnection(connStr))
                {
                    await conn.OpenAsync();
                    using (var cmd = new MySqlCommand($"SHOW COLUMNS FROM `{tableName}`;", conn))
                    using (var rdr = await cmd.ExecuteReaderAsync())
                    {
                        while (await rdr.ReadAsync())
                        {
                            // Coloana "Field" conține numele câmpului
                            columnNames.Add(rdr.GetString("Field"));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Dacă nu putem citi coloanele, revenim la Details cu mesaj de eroare
                TempData["Error"] = $"Nu s-au putut citi coloanele tabelului '{tableName}': {ex.Message}";
                return RedirectToAction("Details", new { tableName });
            }

            if (columnNames.Count == 0)
            {
                TempData["Error"] = $"Tabelul '{tableName}' nu are coloane sau nu există.";
                return RedirectToAction("Details", new { tableName });
            }

            // 2) Construim prompt-ul incluzând explicit lista de coloane
            //    Exemplu: "Generează 5 rânduri INSERT pentru tabela `evenimente` în MariaDB,
            //               folosind coloanele (id, titlu, descriere, data_inceput). Fără alte explicații."
            var columnsList = string.Join(", ", columnNames);
            var promptBuilder = new StringBuilder();
            promptBuilder.AppendLine($"Generează {rowsToGenerate} rânduri INSERT pentru tabela `{tableName}` în MariaDB,");
            promptBuilder.AppendLine($"folosind coloanele ({columnsList}).");
            promptBuilder.Append("Fără alte explicații, doar comenzile INSERT.");

            string generatedScript;
            try
            {
                generatedScript = await _aiService.GenerateAsync(promptBuilder.ToString());
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Eroare la generarea scriptului: {ex.Message}";
                return RedirectToAction("Details", new { tableName });
            }

            // 3) Populăm ViewModel-ul și afișăm view-ul PreviewInsert
            var viewModel = new SqlScriptViewModel
            {
                TableName = tableName,
                GeneratedScript = generatedScript
            };

            return View("PreviewInsert", viewModel);
        }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExecuteInsert(SqlScriptViewModel model)
    {
        if (model == null || string.IsNullOrWhiteSpace(model.TableName))
        {
            ModelState.AddModelError("", "Nu există tabel specificat.");
            return View("PreviewInsert", model);
        }
        if (string.IsNullOrWhiteSpace(model.GeneratedScript))
        {
            ModelState.AddModelError("", "Scriptul SQL este gol.");
            return View("PreviewInsert", model);
        }

        var connStr = _configuration.GetConnectionString("MariaDbConnection");
        try
        {
            using (var conn = new MySqlConnection(connStr))
            {
                await conn.OpenAsync();
                var commands = model.GeneratedScript
                    .Split(';', StringSplitOptions.RemoveEmptyEntries);

                using (var transaction = await conn.BeginTransactionAsync())
                {
                    foreach (var cmdText in commands)
                    {
                        var line = cmdText.Trim();
                        if (string.IsNullOrEmpty(line)) continue;
                        using (var cmd = new MySqlCommand(line + ";", conn, transaction))
                        {
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }
                    await transaction.CommitAsync();
                }
            }
            model.SuccessMessage = $"Scriptul a fost executat cu succes în tabela „{model.TableName}”.";
        }
        catch (Exception ex)
        {
            model.ErrorMessage = $"Eroare la execuție: {ex.Message}";
        }

        return View("PreviewInsert", model);
    }
    }
}
