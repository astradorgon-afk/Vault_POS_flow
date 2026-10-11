using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

sealed class PosSignInLocation
{
    public Guid Id { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public int Kind { get; init; }
}

sealed class ApiResult<T>
{
    public T? Value { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }
    public bool IsSuccess => Error is null && Value is not null;
    public static ApiResult<T> Success(T value) => new() { Value = value };
    public static ApiResult<T> Failure(string error, string? errorCode = null) => new() { Error = error, ErrorCode = errorCode };
}

sealed class Program
{
    static async Task Main()
    {
        using var client = new HttpClient { BaseAddress = new Uri("http://localhost:5177") };
        
        // Test 1: Direct call (like the test console app)
        Console.WriteLine("=== Test 1: Direct call ===");
        try
        {
            var response = await client.GetAsync("/api/v1/locations/sign-in");
            Console.WriteLine($"Status: {response.StatusCode}");
            var content = await response.Content.ReadAsStringAsync();
            Console.WriteLine(content);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine($"Type: {ex.GetType().Name}");
        }
        
        // Test 2: Using GetFromJsonAsync (like the VaultFlowApiClient)
        Console.WriteLine("\n=== Test 2: GetFromJsonAsync ===");
        try
        {
            var result = await client.GetFromJsonAsync<ApiResult<List<PosSignInLocation>>>("/api/v1/locations/sign-in");
            Console.WriteLine($"Result.IsSuccess: {result?.IsSuccess}");
            Console.WriteLine($"Result.Value count: {result?.Value?.Count}");
            Console.WriteLine($"Result.Error: {result?.Error}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine($"Type: {ex.GetType().Name}");
        }
        
        // Test 3: Using SendAsync with relative URI (like VaultFlowApiClient)
        Console.WriteLine("\n=== Test 3: SendAsync with relative URI ===");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/locations/sign-in");
            var response = await client.SendAsync(request);
            Console.WriteLine($"Status: {response.StatusCode}");
            var content = await response.Content.ReadAsStringAsync();
            Console.WriteLine(content);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine($"Type: {ex.GetType().Name}");
        }
    }
}