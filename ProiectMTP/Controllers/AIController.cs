using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ProiectMTP.Services;
using MySqlConnector;

namespace ProiectMTP.Controllers
{
    [Authorize]
    public class AIController : Controller
    {
        private readonly IAIService _aiService;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AIController> _logger;

        public AIController(IAIService aiService, IConfiguration configuration, ILogger<AIController> logger)
        {
            _aiService = aiService;
            _configuration = configuration;
            _logger = logger;
        }

        // =====================================
        // GET: /AI/Index
        // Afișează formularul de input userPrompt
        // =====================================
        public IActionResult Index()
        {
            return View();
        }

        // =====================================
        // POST: /AI/GenerateSQL
        // Primește prompt-ul, generează SQL, execută și afișează rezultatul
        // =====================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateSQL(string userPrompt)
        {
            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                TempData["Error"] = "Trebuie să introduci o instrucțiune.";
                return RedirectToAction("Index");
            }

            // 1) Construiește un prompt instruct clar pentru modelul de bază
            var fullPrompt = $@"
You are an assistant that generates valid SQL for MariaDB. 
Respond **ONLY** with a valid MariaDB statement (no explanations, no comments). 
User instruction (in Romanian): ""{userPrompt}""
";

            string generatedSql;
            try
            {
                generatedSql = await _aiService.GenerateAsync(fullPrompt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Eroare la execuția LocalAIService");
                TempData["Error"] = "A apărut o eroare la generarea SQL-ului. Verifică log-urile.";
                return RedirectToAction("Index");
            }

            // 2) Execută comanda SQL generată (cu validare minimă)
            bool success = false;
            string executionError = null;
            try
            {
                var connStr = _configuration.GetConnectionString("MariaDbConnection");
                using var connection = new MySqlConnection(connStr);
                connection.Open();
                using var cmd = new MySqlCommand(generatedSql, connection);
                cmd.ExecuteNonQuery();
                success = true;
            }
            catch (Exception ex)
            {
                executionError = ex.Message;
            }

            // 3) Pregătește datele pentru view: SQL-ul generat + rezultatul execuției
            ViewBag.GeneratedSql = generatedSql;
            ViewBag.ExecutionSuccess = success;
            ViewBag.ExecutionError = executionError;

            return View("Result");
        }
    }
}
