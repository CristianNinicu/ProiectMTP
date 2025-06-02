namespace ProiectMTP.Models
{
    public class SqlScriptViewModel
    {
        public string TableName { get; set; }
        public string GeneratedScript { get; set; }
        public string ErrorMessage { get; set; }
        public string SuccessMessage { get; set; }
    }
}