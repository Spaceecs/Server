using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using System.Threading.Tasks;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

// Server side
internal class Program
{
    private static string logFilePath = "server_log.txt";
    private static Dictionary<string, (int requestCount, DateTime lastRequestTime)> clientRequestStats = new Dictionary<string, (int, DateTime)>();
    private static int maxRequestsPerMinute = 5;  // Maximum requests per client per minute
    private static TimeSpan requestTimeWindow = TimeSpan.FromMinutes(1);  // Time window for request count
    private static int maxClients = 10;  // Maximum number of concurrent client connections
    private static SemaphoreSlim connectionSemaphore = new SemaphoreSlim(maxClients, maxClients);

    private static async Task Main(string[] args)
    {
        int port = 6000;
        TcpListener server = new TcpListener(IPAddress.Any, port);
        server.Start();
        Console.WriteLine("Server started. Waiting for clients to connect...");

        while (true)
        {
            TcpClient client = await server.AcceptTcpClientAsync();
            string clientEndPoint = client.Client.RemoteEndPoint.ToString();
            string connectTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // Check if the server has reached the max client limit
            if (!await connectionSemaphore.WaitAsync(TimeSpan.FromSeconds(1)))  // Try acquiring a slot with a timeout
            {
                NetworkStream stream = client.GetStream();
                byte[] response = Encoding.ASCII.GetBytes("Server is currently at full capacity. Please try again later.");
                stream.Write(BitConverter.GetBytes(response.Length), 0, 4);
                stream.Write(response, 0, response.Length);
                client.Close();
                Console.WriteLine($"New connection attempt from {clientEndPoint} rejected. Server is full.");
                continue;
            }

            Console.WriteLine($"{clientEndPoint} connected at {connectTime}");
            NetworkStream clientStream = client.GetStream();

            try
            {
                // Check if client exceeds the request limit
                if (IsRequestLimitExceeded(clientEndPoint))
                {
                    // Notify the client and disconnect
                    byte[] response = Encoding.ASCII.GetBytes("Request limit exceeded. Please try again in a minute.");
                    clientStream.Write(BitConverter.GetBytes(response.Length), 0, 4);
                    clientStream.Write(response, 0, response.Length);
                    Console.WriteLine($"{clientEndPoint} exceeded request limit. Connection rejected.");
                    client.Close();
                    continue;
                }

                // Update request count and timestamp
                UpdateRequestStats(clientEndPoint);

                // Load XML content
                string url = "https://bank.gov.ua/NBUStatService/v1/statdirectory/exchange";
                string xmlContent = await GetXmlContentAsync(url);
                List<CurrencyRate> currencyRates = ParseCurrencyRates(xmlContent);

                // Add UAH to the currency list
                if (currencyRates.Find(c => c.CurrencyCode == "UAH") == null)
                {
                    currencyRates.Add(new CurrencyRate
                    {
                        CurrencyCode = "UAH",
                        Rate = 1m // UAH to UAH rate
                    });
                }

                LogConnection(clientEndPoint, connectTime, currencyRates, "connected");

                while (true)
                {
                    // Read currency codes from client
                    string currencyCode1 = ReadString(clientStream, ReadInt(clientStream));
                    string currencyCode2 = ReadString(clientStream, ReadInt(clientStream));

                    decimal result = 0;
                    var rate1 = currencyRates.Find(c => c.CurrencyCode == currencyCode1);
                    var rate2 = currencyRates.Find(c => c.CurrencyCode == currencyCode2);

                    if (rate1 == null || rate2 == null)
                    {
                        Console.WriteLine("One or both currency codes were not found or invalid");
                        result = 0;
                    }
                    else
                    {
                        // Calculate exchange rate
                        result = CalculateExchangeRate(rate1, rate2, currencyCode1, currencyCode2);
                    }

                    Console.WriteLine($"Client request: {currencyCode1} to {currencyCode2} : {result}");
                    byte[] resultBytes = Encoding.ASCII.GetBytes(result.ToString());
                    clientStream.Write(BitConverter.GetBytes(resultBytes.Length), 0, 4);
                    clientStream.Write(resultBytes, 0, resultBytes.Length);

                    LogClientRequest(clientEndPoint, currencyCode1, currencyCode2, result);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing request: {ex.Message}");
            }
            finally
            {
                string disconnectTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                clientStream.Close();
                client.Close();
                connectionSemaphore.Release();  // Release the semaphore slot
                Console.WriteLine($"{clientEndPoint} disconnected at {disconnectTime}");
                LogConnection(clientEndPoint, disconnectTime, null, "disconnected");
            }
        }
    }

    private static bool IsRequestLimitExceeded(string clientEndPoint)
    {
        if (clientRequestStats.ContainsKey(clientEndPoint))
        {
            var (requestCount, lastRequestTime) = clientRequestStats[clientEndPoint];
            if (DateTime.Now - lastRequestTime < requestTimeWindow)
            {
                return requestCount >= maxRequestsPerMinute;
            }
        }
        return false;
    }

    private static void UpdateRequestStats(string clientEndPoint)
    {
        if (clientRequestStats.ContainsKey(clientEndPoint))
        {
            var (requestCount, lastRequestTime) = clientRequestStats[clientEndPoint];
            if (DateTime.Now - lastRequestTime > requestTimeWindow)
            {
                clientRequestStats[clientEndPoint] = (1, DateTime.Now); // Reset count and set timestamp
            }
            else
            {
                clientRequestStats[clientEndPoint] = (requestCount + 1, DateTime.Now); // Increment count
            }
        }
        else
        {
            clientRequestStats[clientEndPoint] = (1, DateTime.Now); // New client
        }
    }

    private static decimal CalculateExchangeRate(CurrencyRate rate1, CurrencyRate rate2, string currencyCode1, string currencyCode2)
    {
        decimal result = 0;

        // If both currencies are UAH
        if (currencyCode1 == "UAH" && currencyCode2 == "UAH")
        {
            result = 1;
        }
        // If the first currency is UAH
        else if (currencyCode1 == "UAH")
        {
            result = 1 / rate2.Rate;
        }
        // If the second currency is UAH
        else if (currencyCode2 == "UAH")
        {
            result = rate1.Rate;
        }
        // Neither currency is UAH
        else
        {
            result = rate1.Rate / rate2.Rate;
        }

        return result;
    }

    private static void LogConnection(string clientEndPoint, string time, List<CurrencyRate> currencyRates, string status)
    {
        using (StreamWriter writer = new StreamWriter(logFilePath, true))
        {
            writer.WriteLine($"{time} - {clientEndPoint} {status}");
            if (status == "connected" && currencyRates != null)
            {
                writer.WriteLine("Currency Rates at connection:");
                foreach (var rate in currencyRates)
                {
                    writer.WriteLine($"Currency: {rate.CurrencyCode}, Rate: {rate.Rate}");
                }
            }
            writer.WriteLine("=====================================");
        }
    }

    private static void LogClientRequest(string clientEndPoint, string currencyCode1, string currencyCode2, decimal result)
    {
        using (StreamWriter writer = new StreamWriter(logFilePath, true))
        {
            writer.WriteLine($"Client request from {clientEndPoint}: {currencyCode1} to {currencyCode2} : {result}");
        }
    }

    private static int ReadInt(NetworkStream stream)
    {
        byte[] buffer = new byte[4];
        stream.Read(buffer, 0, 4);
        return BitConverter.ToInt32(buffer, 0);
    }

    private static string ReadString(NetworkStream stream, int length)
    {
        byte[] buffer = new byte[length];
        stream.Read(buffer, 0, length);
        return Encoding.ASCII.GetString(buffer);
    }

    public static async Task<string> GetXmlContentAsync(string url)
    {
        using (HttpClient client = new HttpClient())
        {
            string xmlContent = await client.GetStringAsync(url);
            return xmlContent;
        }
    }

    public static List<CurrencyRate> ParseCurrencyRates(string xmlContent)
    {
        XDocument xdoc = XDocument.Parse(xmlContent);

        List<CurrencyRate> currencyRates = new List<CurrencyRate>();
        foreach (var element in xdoc.Descendants("currency"))
        {
            string currencyCode = element.Element("cc")?.Value;
            decimal rate = decimal.Parse(element.Element("rate")?.Value ?? "0", CultureInfo.InvariantCulture);

            if (!string.IsNullOrEmpty(currencyCode))
            {
                currencyRates.Add(new CurrencyRate
                {
                    CurrencyCode = currencyCode,
                    Rate = rate
                });
            }
        }
        return currencyRates;
    }
}

public class CurrencyRate
{
    public string CurrencyCode { get; set; }
    public decimal Rate { get; set; }
}
