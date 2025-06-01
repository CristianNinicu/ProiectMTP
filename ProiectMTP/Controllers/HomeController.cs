using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using ProiectMTP.Models;

namespace ProiectMTP.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<HomeController> _logger;

        public HomeController(IConfiguration configuration, ILogger<HomeController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        // =====================================
        //            ACTION: Index
        // =====================================
        public IActionResult Index()
        {
            if (!User.Identity.IsAuthenticated)
            {
                return RedirectToAction("Login", "Account");
            }

            var tables = new List<string>();
            string connectionString = _configuration.GetConnectionString("MariaDbConnection");

            using (var connection = new MySqlConnection(connectionString))
            {
                connection.Open();
                using (var cmd = new MySqlCommand("SHOW TABLES;", connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        tables.Add(reader.GetString(0));
                    }
                }
            }

            // Trimitem lista de tabele în view
            return View(tables);
        }

        // =====================================
        //          ACTION: CreateTable
        // =====================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult CreateTable(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                TempData["Error"] = "Numele tabelului este obligatoriu.";
                return RedirectToAction("Index");
            }

            string connectionString = _configuration.GetConnectionString("MariaDbConnection");

            try
            {
                using (var connection = new MySqlConnection(connectionString))
                {
                    connection.Open();
                    // Creăm tabela doar cu coloana Id
                    string sqlCreate = $@"
                        CREATE TABLE `{tableName}` (
                            Id INT PRIMARY KEY AUTO_INCREMENT
                        );
                    ";
                    using (var cmd = new MySqlCommand(sqlCreate, connection))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }

                TempData["Success"] = $"Tabelul '{tableName}' a fost creat (cu coloana Id).";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Eroare la crearea tabelului: {ex.Message}";
                return RedirectToAction("Index");
            }

            // După creare, redirecționăm către EditTable ca să introduci coloanele
            return RedirectToAction("EditTable", new { tableName = tableName });
        }

        // =====================================
        //          ACTION: DropTable
        // =====================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DropTable(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                TempData["Error"] = "Nu a fost selectat niciun tabel.";
                return RedirectToAction("Index");
            }

            string connectionString = _configuration.GetConnectionString("MariaDbConnection");
            try
            {
                using (var connection = new MySqlConnection(connectionString))
                {
                    connection.Open();
                    string sqlDrop = $"DROP TABLE `{tableName}`;";
                    using (var cmd = new MySqlCommand(sqlDrop, connection))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }

                TempData["Success"] = $"Tabelul '{tableName}' a fost șters cu succes.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Eroare la ștergerea tabelului: {ex.Message}";
            }

            return RedirectToAction("Index");
        }

        // =====================================
        //         ACTION: EditTable
        // =====================================
        public IActionResult EditTable(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                TempData["Error"] = "Nu a fost specificat niciun tabel.";
                return RedirectToAction("Index");
            }

            var columns = new List<string>();
            string connectionString = _configuration.GetConnectionString("MariaDbConnection");

            using (var connection = new MySqlConnection(connectionString))
            {
                connection.Open();
                string query = $"SHOW COLUMNS FROM `{tableName}`;";
                using (var cmd = new MySqlCommand(query, connection))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        columns.Add(reader.GetString("Field"));
                    }
                }
            }

            ViewBag.TableName = tableName;
            return View(columns);
        }

        // =====================================
        //         ACTION: AddColumn
        //      (rămâne neschimbată – vezi anterior)
        // =====================================
        [HttpPost]
        public IActionResult AddColumn(string tableName, string columnName, string columnType)
        {
            if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(columnType))
            {
                TempData["Error"] = "Completează toate câmpurile.";
                return RedirectToAction("EditTable", new { tableName });
            }

            // Exemplu de validare suplimentară
            var allowedTypes = new[]
            {
                "INT", "BIGINT", "VARCHAR(50)", "VARCHAR(100)", "TEXT",
                "DATE", "DATETIME", "DECIMAL(10,2)", "FLOAT", "BIT"
            };
            if (!allowedTypes.Contains(columnType))
            {
                TempData["Error"] = "Tipul de coloană selectat nu este valid.";
                return RedirectToAction("EditTable", new { tableName });
            }

            string connectionString = _configuration.GetConnectionString("MariaDbConnection");
            try
            {
                using (var connection = new MySqlConnection(connectionString))
                {
                    connection.Open();
                    string sqlAlter = $"ALTER TABLE `{tableName}` ADD `{columnName}` {columnType};";
                    using (var cmd = new MySqlCommand(sqlAlter, connection))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
                TempData["Success"] = $"Coloana '{columnName}' a fost adăugată.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Eroare la adăugarea coloanei: {ex.Message}";
            }

            return RedirectToAction("EditTable", new { tableName });
        }

        // =====================================
        //       ACTION: DeleteColumn
        //      (rămâne neschimbată)
        // =====================================
        [HttpPost]
        public IActionResult DeleteColumn(string tableName, string columnName)
        {
            string connectionString = _configuration.GetConnectionString("MariaDbConnection");
            using (var connection = new MySqlConnection(connectionString))
            {
                connection.Open();
                string sql = $"ALTER TABLE `{tableName}` DROP COLUMN `{columnName}`;";
                using (var cmd = new MySqlCommand(sql, connection))
                {
                    cmd.ExecuteNonQuery();
                }
            }

            TempData["Success"] = $"Coloana '{columnName}' a fost ștearsă.";
            return RedirectToAction("EditTable", new { tableName });
        }

        // =====================================
        //     ACTION: RenameColumn
        //      (rămâne neschimbată)
        // =====================================
        [HttpPost]
        public IActionResult RenameColumn(string tableName, string oldColumnName, string newColumnName, string newColumnType)
        {
            if (string.IsNullOrWhiteSpace(newColumnName) || string.IsNullOrWhiteSpace(newColumnType))
            {
                TempData["Error"] = "Toate câmpurile sunt obligatorii.";
                return RedirectToAction("EditTable", new { tableName });
            }

            string connectionString = _configuration.GetConnectionString("MariaDbConnection");
            try
            {
                using (var connection = new MySqlConnection(connectionString))
                {
                    connection.Open();
                    string sql = $"ALTER TABLE `{tableName}` CHANGE `{oldColumnName}` `{newColumnName}` {newColumnType};";
                    using (var cmd = new MySqlCommand(sql, connection))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
                TempData["Success"] = $"Coloana '{oldColumnName}' a fost redenumită în '{newColumnName}' și tipul schimbat.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Eroare la redenumirea coloanei: {ex.Message}";
            }

            return RedirectToAction("EditTable", new { tableName });
        }
    }
}
