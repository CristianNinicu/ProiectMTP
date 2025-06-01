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

        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

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
                var cmd = new MySqlCommand("SHOW TABLES;", connection);
                var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            return View(tables);
        }

        [HttpPost]
        public IActionResult CreateTable(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName))
            {
                TempData["Error"] = "Numele tabelului este obligatoriu.";
                return RedirectToAction("Index");
            }

            string connectionString = _configuration.GetConnectionString("MariaDbConnection");

            using (var connection = new MySqlConnection(connectionString))
            {
                connection.Open();
                var sql = $@"
                    CREATE TABLE `{tableName}` (
                        Id INT PRIMARY KEY AUTO_INCREMENT,
                        Nume VARCHAR(100),
                        Email VARCHAR(100),
                        Telefon VARCHAR(20)
                    );";

                var cmd = new MySqlCommand(sql, connection);
                cmd.ExecuteNonQuery();
            }

            TempData["Success"] = $"Tabelul '{tableName}' a fost creat.";
            return RedirectToAction("Index");
        }
        public IActionResult EditTable(string tableName)
        {
            var columns = new List<string>();
            string connectionString = _configuration.GetConnectionString("MariaDbConnection");

            using (var connection = new MySqlConnection(connectionString))
            {
                connection.Open();
                string query = $"SHOW COLUMNS FROM `{tableName}`;";
                var cmd = new MySqlCommand(query, connection);
                var reader = cmd.ExecuteReader();

                while (reader.Read())
                {
                    columns.Add(reader.GetString("Field"));
                }
            }

            ViewBag.TableName = tableName;
            return View(columns);
        }

        [HttpPost]
        public IActionResult AddColumn(string tableName, string columnName, string columnType)
        {
            if (string.IsNullOrWhiteSpace(columnName) || string.IsNullOrWhiteSpace(columnType))
            {
                TempData["Error"] = "Completeaza toate campurile.";
                return RedirectToAction("EditTable", new { tableName });
            }

            string connectionString = _configuration.GetConnectionString("MariaDbConnection");

            using (var connection = new MySqlConnection(connectionString))
            {
                connection.Open();
                string sql = $"ALTER TABLE `{tableName}` ADD `{columnName}` {columnType};";
                var cmd = new MySqlCommand(sql, connection);
                cmd.ExecuteNonQuery();
            }

            TempData["Success"] = $"Coloana '{columnName}' a fost adaugata.";
            return RedirectToAction("EditTable", new { tableName });
        }

        [HttpPost]
        public IActionResult DeleteColumn(string tableName, string columnName)
        {
            string connectionString = _configuration.GetConnectionString("MariaDbConnection");

            using (var connection = new MySqlConnection(connectionString))
            {
                connection.Open();
                string sql = $"ALTER TABLE `{tableName}` DROP COLUMN `{columnName}`;";
                var cmd = new MySqlCommand(sql, connection);
                cmd.ExecuteNonQuery();
            }

            TempData["Success"] = $"Coloana '{columnName}' a fost stearsa.";
            return RedirectToAction("EditTable", new { tableName });
        }
        [HttpPost]
        public IActionResult RenameColumn(string tableName, string oldColumnName, string newColumnName, string newColumnType)
        {
            if (string.IsNullOrWhiteSpace(newColumnName) || string.IsNullOrWhiteSpace(newColumnType))
            {
                TempData["Error"] = "Toate campurile sunt obligatorii.";
                return RedirectToAction("EditTable", new { tableName });
            }

            string connectionString = _configuration.GetConnectionString("MariaDbConnection");

            using (var connection = new MySqlConnection(connectionString))
            {
                connection.Open();

                string sql = $"ALTER TABLE `{tableName}` CHANGE `{oldColumnName}` `{newColumnName}` {newColumnType};";
                var cmd = new MySqlCommand(sql, connection);
                cmd.ExecuteNonQuery();
            }

            TempData["Success"] = $"Coloana '{oldColumnName}' a fost redenumita in '{newColumnName}' si tipul schimbat.";
            return RedirectToAction("EditTable", new { tableName });
        }

    }
}
