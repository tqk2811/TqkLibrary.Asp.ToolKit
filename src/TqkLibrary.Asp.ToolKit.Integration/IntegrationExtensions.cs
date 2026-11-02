using TqkLibrary.Asp.ToolKit.Integration.Models;
using System.Net;

namespace TqkLibrary.Asp.ToolKit.Integration
{
    public static class IntegrationExtensions
    {
        /// <summary>
        /// Converts a <see cref="CookieContainer"/> to a list of <see cref="CookieModel"/> for storage.
        /// </summary>
        public static IReadOnlyList<CookieModel> ToCookieModels(this CookieContainer container)
            => container.GetAllCookies().ToCookieModels();

        /// <summary>
        /// Converts a <see cref="CookieCollection"/> to a list of <see cref="CookieModel"/> for storage.
        /// </summary>
        public static IReadOnlyList<CookieModel> ToCookieModels(this CookieCollection cookies)
        {
            var result = new List<CookieModel>();
            foreach (Cookie cookie in cookies)
                result.Add(CookieModel.FromCookie(cookie));
            return result;
        }

        /// <summary>
        /// Loads a list of <see cref="CookieModel"/> into a new <see cref="CookieContainer"/>.
        /// </summary>
        public static CookieContainer ToCookieContainer(this IEnumerable<CookieModel> models)
        {
            var container = new CookieContainer();
            container.LoadCookies(models);
            return container;
        }

        /// <summary>
        /// Loads a list of <see cref="CookieModel"/> into an existing <see cref="CookieContainer"/>.
        /// </summary>
        public static void LoadCookies(this CookieContainer container, IEnumerable<CookieModel> models)
        {
            foreach (var model in models)
                container.Add(model.ToCookie());
        }
    }
}
