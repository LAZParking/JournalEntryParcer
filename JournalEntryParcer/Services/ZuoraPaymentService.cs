using JournalEntryParcer.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RestSharp;

namespace JournalEntryParcer.Services
{
    public class ZuoraPaymentService
    {
        private readonly ZuoraTokenService _tokenService;
        private readonly string _baseUrl;
        private readonly ILogger<ZuoraPaymentService> _logger;

        public ZuoraPaymentService(ZuoraTokenService tokenService, IConfiguration config, ILogger<ZuoraPaymentService> logger)
        {
            _tokenService = tokenService;
            _baseUrl = config["Zuora:BaseUrl"]!;
            _logger = logger;
        }

        public async Task<string> CreatePaymentAsync(PaymentHeader paymentHeader, CustomerAccountHeader cah)
        {
            var token = await _tokenService.GetTokenAsync();
            var (bankPaymentType, bankLast4, lockBoxId) = ParsePaymentType(cah.paymentType);

            var body = new Dictionary<string, object?>
            {
                ["accountNumber"]      = cah.accountNumber,
                ["amount"]             = cah.creditValue,
                ["currency"]           = paymentHeader.currency ?? "USD",
                ["effectiveDate"]      = cah.postingDate?.ToString("yyyy-MM-dd"),
                ["type"]               = "External",
                ["paymentMethodType"]  = "Check",
                ["referenceId"]        = cah.paymentReference,
                ["comment"]            = cah.paymentName,
                ["BankPaymentType__c"] = bankPaymentType,
                ["BankNumber__c"]      = bankLast4,
                ["LockBoxID__c"]       = lockBoxId,
                ["BLPaymentRef__c"]    = cah.allocationID?.ToString()
            };

            var client = new RestClient(_baseUrl);
            var request = new RestRequest("/v1/payments", Method.Post);
            request.AddHeader("Authorization", $"Bearer {token}");
            request.AddJsonBody(body);

            var response = await client.ExecuteAsync(request);

            _logger.LogInformation("Create payment response ({StatusCode}): {Body}", response.StatusCode, response.Content);

            if (!response.IsSuccessful)
                throw new Exception($"Create payment failed ({response.StatusCode}): {response.Content}");

            var json = System.Text.Json.JsonDocument.Parse(response.Content!);
            if (!json.RootElement.TryGetProperty("id", out var idElement))
                throw new Exception($"Zuora response did not include a payment ID. Response: {response.Content}");

            return idElement.GetString()
                ?? throw new Exception("Zuora payment ID was null.");
        }

        public async Task ApplyPaymentAsync(string paymentId, List<Transaction> transactions)
        {
            if (transactions.Count == 0) return;

            var token = await _tokenService.GetTokenAsync();

            var effectiveDate = transactions.First().invoiceDate
                ?? throw new Exception("Transaction invoiceDate is null; cannot apply payment.");

            var invoices = transactions
                .Select(t => new { invoiceNumber = t.invoiceNumber, amount = t.amountToAllocate })
                .ToList();

            var client = new RestClient(_baseUrl);
            var request = new RestRequest($"/v1/payments/{paymentId}/apply", Method.Put);
            request.AddHeader("Authorization", $"Bearer {token}");
            request.AddJsonBody(new
            {
                effectiveDate = effectiveDate.ToString("yyyy-MM-dd"),
                invoices
            });

            var response = await client.ExecuteAsync(request);

            _logger.LogInformation("Apply payment response ({StatusCode}): {Body}", response.StatusCode, response.Content);

            if (!response.IsSuccessful)
                throw new Exception($"Apply payment failed ({response.StatusCode}): {response.Content}");

            var applyJson = System.Text.Json.JsonDocument.Parse(response.Content!);
            if (applyJson.RootElement.TryGetProperty("success", out var successProp) && !successProp.GetBoolean())
                throw new Exception($"Apply payment returned success=false: {response.Content}");

            _logger.LogInformation("Applied {Count} invoice(s) to payment {PaymentId}", invoices.Count, paymentId);
        }

        private static (string bankPaymentType, string bankLast4, string lockBoxId) ParsePaymentType(string? paymentType)
        {
            if (string.IsNullOrEmpty(paymentType)) return ("", "", "");
            var parts = paymentType.Split("::");
            return (
                parts.ElementAtOrDefault(0) ?? "",
                parts.ElementAtOrDefault(1) ?? "",
                parts.ElementAtOrDefault(2) ?? ""
            );
        }
    }
}
