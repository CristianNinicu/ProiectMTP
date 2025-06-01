using System.ComponentModel.DataAnnotations;

public class Client
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Numele este obligatoriu")]
    public string Name { get; set; }

    [Required, EmailAddress(ErrorMessage = "Email invalid")]
    public string Email { get; set; }

    public string Phone { get; set; }
}
