using TqkLibrary.Asp.ToolKit.Integration.Models;
using System.Net;

namespace TqkLibrary.Asp.ToolKit.Integration
{
    public static class IntegrationExtensions
    {
        /// <summary>
        /// Chuyển <see cref="CookieContainer"/> sang danh sách <see cref="CookieModel"/> để lưu trữ.
        /// </summary>
        public static IReadOnlyList<CookieModel> ToCookieModels(this CookieContainer container)
            => container.GetAllCookies().ToCookieModels();

        /// <summary>
        /// Chuyển <see cref="CookieCollection"/> sang danh sách <see cref="CookieModel"/> để lưu trữ.
        /// </summary>
        public static IReadOnlyList<CookieModel> ToCookieModels(this CookieCollection cookies)
        {
            var result = new List<CookieModel>();
            foreach (Cookie cookie in cookies)
                result.Add(CookieModel.FromCookie(cookie));
            return result;
        }

        /// <summary>
        /// Nạp danh sách <see cref="CookieModel"/> vào <see cref="CookieContainer"/> mới.
        /// </summary>
        public static CookieContainer ToCookieContainer(this IEnumerable<CookieModel> models)
        {
            var container = new CookieContainer();
            container.LoadCookies(models);
            return container;
        }

        /// <summary>
        /// Nạp danh sách <see cref="CookieModel"/> vào <see cref="CookieContainer"/> hiện có.
        /// </summary>
        public static void LoadCookies(this CookieContainer container, IEnumerable<CookieModel> models)
        {
            foreach (var model in models)
                container.Add(model.ToCookie());
        }
    }
}
