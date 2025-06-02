namespace ProiectMTP.Controllers
{
    using Microsoft.AspNetCore.Mvc;
    using System.Xml.Linq;
    using Microsoft.AspNetCore.Authentication;
    using Microsoft.AspNetCore.Authentication.Cookies;
    using System.Security.Claims;
    using ProiectMTP.Models;
    using Microsoft.AspNetCore.Authorization;

    [Authorize]
    [AllowAnonymous]
    public class AccountController : Controller
    {
        private readonly string xmlPath;

        public AccountController()
        {
            xmlPath = Path.Combine(Directory.GetCurrentDirectory(), "users.xml");
        }
        
        [HttpGet]
        [AllowAnonymous]
        public IActionResult Login(string returnUrl = null)
        {
            ViewBag.Message = TempData["Message"];
            ViewData["ReturnUrl"] = returnUrl;
            return View();
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(string username, string password, string returnUrl = null)
        {
            ViewData["ReturnUrl"] = returnUrl;

            if (!System.IO.File.Exists(xmlPath))
            {
                TempData["Message"] = "Fișierul de utilizatori nu a fost găsit.";
                return RedirectToAction("Login");
            }

            var usersXml = XDocument.Load(xmlPath)
                                     .Root
                                     .Elements("User")
                                     .Select(x => new User
                                     {
                                         Username = x.Element("Username")?.Value,
                                         Password = x.Element("Password")?.Value
                                     })
                                     .ToList();

            var user = usersXml.FirstOrDefault(u => u.Username == username && u.Password == password);
            if (user != null)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, user.Username)
                };

                var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

                var authProperties = new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30)
                };

                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(claimsIdentity),
                    authProperties);

                if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                {
                    return Redirect(returnUrl);
                }
                else
                {
                    return RedirectToAction("Index", "Home");
                }
            }

            TempData["Message"] = "Autentificare eșuată: username/parolă incorecte.";
            return RedirectToAction("Login");
        }

        [HttpPost]
        [Authorize]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Login");
        }
    }

}
