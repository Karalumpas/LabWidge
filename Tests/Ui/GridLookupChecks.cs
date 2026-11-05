using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Reflection;
using System.Windows.Forms;

internal static partial class UiChecks
{
    /// <summary>Answers like Green Power Denmark's address lookup and Energinet's price list, so the check needs no network.</summary>
    private sealed class GridLookupHandler : HttpMessageHandler
    {
        public string LookupAnswer { get; set; } = "null";
        public List<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            var body = url.Contains("supplierlookup") ? LookupAnswer
                : url.Contains("DatahubPricelist") ? CeriusTariffs
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }

        private static readonly string CeriusTariffs = "{\"records\":[{\"ChargeOwner\":\"Cerius A/S\",\"ChargeType\":\"D03\",\"ChargeTypeCode\":\"30TR_C_ET\","
            + "\"Note\":\"Nettarif C time\",\"ValidFrom\":\"2020-01-01T00:00:00\",\"ValidTo\":null,"
            + string.Join(",", Enumerable.Range(1, 24).Select(h => $"\"Price{h}\":{(h is >= 18 and <= 21 ? "0.6" : h <= 6 ? "0.1" : "0.2")}"))
            + "}]}";
    }

    /// <summary>
    /// The setup guide's price page: an address finds the grid company and its tariff, sets the price area from the
    /// postal code, saves the company and keeps no address. A wrong address says so instead of guessing.
    /// </summary>
    private static void GridLookupChecks()
    {
        var handler = new GridLookupHandler();
        var oldHttp = PricePage.Http;
        var cache = typeof(PricePage).GetField("_companyCache", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldCache = cache.GetValue(null);
        PricePage.Http = new HttpClient(handler);
        cache.SetValue(null, new List<string> { "Cerius A/S", "N1 A/S - 344", "Radius Elnet A/S", "TREFOR El-net A/S" });
        L.Use(L.Danish);
        try
        {
            var settings = new AppSettings { Country = "DK", PriceArea = "DK1" };
            using var form = new OnboardingForm(settings);
            form.Show();
            Application.DoEvents();
            var pages = Get<SettingsPage[]>(form, "_pages");
            var price = pages.OfType<PricePage>().Single();
            ((Task)Call(form, "GoToAsync", Array.IndexOf(pages, price))!).GetAwaiter();
            PumpUntil(() => Get<int>(form, "_index") == Array.IndexOf(pages, price));

            var address = Get<TextBox>(price, "_address");
            var status = Get<Label>(price, "_addressStatus");
            var tariffStatus = Get<Label>(price, "_companyStatus");
            var company = Get<ComboBox>(price, "_company");
            var tariff = Get<ComboBox>(price, "_tariff");
            Check(address.Visible && Get<Button>(price, "_find").Visible, "The address field is not shown for Denmark.");

            // Not found: says so and chooses nothing
            address.Text = "Ingenvej 1, 4000 Roskilde";
            Call(price, "FindCompanyAsync");
            PumpUntil(() => status.Text.Contains("ikke fundet"));
            Check(company.Text.Length == 0, "A grid company was chosen for an address that was not found.");

            // No postal code anywhere: asks for it without a request
            var requests = handler.Requests.Count;
            Get<TextBox>(price, "_postal").Text = "";
            address.Text = "Vestergade 1";
            Call(price, "FindCompanyAsync");
            Application.DoEvents();
            Check(handler.Requests.Count == requests && status.Text.Contains("postnummer"), "An address without postal code was looked up.");

            // Found: company, tariff and price area are chosen
            handler.LookupAnswer = "{\"name\":\"Cerius A/S\",\"phone\":\"(+45) 70 29 20 24\",\"website\":\"www.cerius.dk\",\"def\":\"740\"}";
            address.Text = "Ringstedgade 66, 1. th, 4000 Roskilde";
            Call(price, "FindCompanyAsync");
            PumpUntil(() => tariff.SelectedItem != null && tariffStatus.Text.StartsWith("✓"));
            Check(handler.Requests.Any(r => r.EndsWith("supplierlookup/Ringstedgade%2066%204000")), "The lookup was not asked for street, number and postal code only.");
            Check(company.Text == "Cerius A/S", $"The grid company was not chosen ({company.Text}).");
            Check(Get<TextBox>(price, "_postal").Text == "4000" && Get<RadioButton>(price, "_dk2").Checked && !Get<RadioButton>(price, "_dk1").Checked,
                "The postal code and price area did not follow the address.");
            Check(status.Text.Contains("Cerius A/S") && tariffStatus.Text.Contains("Netselskab og tarif fundet"), $"The status does not say what was found: {status.Text} / {tariffStatus.Text}");

            // The price areas sit in separate rows: choosing one by hand clears the other
            Get<RadioButton>(price, "_dk1").Checked = true;
            Check(!Get<RadioButton>(price, "_dk2").Checked, "Both price areas were checked.");
            Get<RadioButton>(price, "_dk2").Checked = true;

            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                Directory.CreateDirectory(Path.Combine(Output, "settings"));
                bitmap.Save(Path.Combine(Output, "settings", "onboarding-grid-lookup.png"), ImageFormat.Png);
            }

            var saved = new AppSettings { Country = "DK" };
            Check(price.SaveTo(saved) == null, "The page did not save after the lookup.");
            Check(saved.NetTariffOwner == "Cerius A/S" && saved.NetTariffCodes.SequenceEqual(new[] { "30TR_C_ET" }) && saved.PostalCode == "4000",
                "The found grid company and tariff were not saved.");
            Check(!System.Text.Json.JsonSerializer.Serialize(saved).Contains("Ringstedgade"), "The address was saved.");
            form.Close();
        }
        finally
        {
            PricePage.Http = oldHttp;
            cache.SetValue(null, oldCache);
            L.Use(L.English);
        }
        Console.WriteLine("PASS UI: the guide finds the grid company and tariff from an address, sets the price area and keeps no address");
    }
}
