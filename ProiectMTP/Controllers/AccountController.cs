namespace ProiectMTP.Controllers
{
    using Microsoft.AspNetCore.Mvc;
    using System.Xml.Linq;
    using Microsoft.AspNetCore.Authentication;
    using Microsoft.AspNetCore.Authentication.Cookies;
    using System.Security.Claims;
    using ProiectMTP.Models;

    public class AccountController : Controller
    {
        private readonly string xmlPath;

        public AccountController()
        {
            xmlPath = Path.Combine(Directory.GetCurrentDirectory(), "users.xml");
        }
        [HttpGet]
        public IActionResult Login()
        {
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> Login(string username, string password)
        {
            var users = XDocument.Load(xmlPath).Root.Elements("User")
                .Select(x => new User
                {
                    Username = x.Element("Username")?.Value,
                    Password = x.Element("Password")?.Value
                }).ToList();

            var user = users.FirstOrDefault(u => u.Username == username && u.Password == password);

            if (user != null)
            {
                var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.Username)
            };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity));

                return RedirectToAction("Index", "Home");
            }

            ViewBag.Message = "Autentificare eșuată";
            return View();
        }

        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync();
            return RedirectToAction("Login");
        }
    }

}
