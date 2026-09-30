using AIEnabledRma.Domain.Abstractions;
using AIEnabledRma.Domain.Catalog;
using AIEnabledRma.Domain.Common;
using AIEnabledRma.Domain.Customers;
using AIEnabledRma.Domain.Rma;
using AIEnabledRma.Domain.Rules;
using AIEnabledRma.Rag.Knowledge;

namespace AIEnabledRma.Tests;

/// <summary>Builders for the domain entities the tests need, kept in one place so each
/// test only states the field it actually cares about.</summary>
internal static class TestData
{
    public static Device Device(
        string serial = "SN-1",
        bool withWarranty = true,
        DateOnly? start = null,
        DateOnly? end = null,
        bool warrantyActive = true,
        string tier = "standard",
        DateTimeOffset? shipped = null,
        Guid? id = null)
    {
        var deviceId = id ?? Guid.NewGuid();

        return new Device
        {
            Id = deviceId,
            ProductId = Guid.NewGuid(),
            SerialNumber = serial,
            MacAddress = "A4C1F2E30001",
            Imei = "356938035643809",
            ShippedAtUtc = shipped,
            Warranties = withWarranty
                ?
                [
                    new Warranty
                    {
                        Id = Guid.NewGuid(),
                        DeviceId = deviceId,
                        PlanName = "Standard Coverage",
                        Tier = tier,
                        StartDate = start ?? new DateOnly(2024, 1, 1),
                        EndDate = end ?? new DateOnly(2027, 1, 1),
                        IsActive = warrantyActive,
                    },
                ]
                : [],
        };
    }

    public static Customer Customer(
        string first = "Ada",
        string last = "Lovelace",
        string email = "ada@example.com",
        Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        FirstName = first,
        LastName = last,
        Email = email,
        PhoneNumber = "+44 20 7946 0001",
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    public static Address Address(
        string label = "HQ",
        string line1 = "1 Infinite Loop",
        string? line2 = null,
        string city = "Cupertino",
        string state = "CA",
        string postal = "95014",
        string country = "US",
        bool isDefault = true,
        Guid? customerId = null) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = customerId,
        Label = label,
        Line1 = line1,
        Line2 = line2,
        City = city,
        StateOrProvince = state,
        PostalCode = postal,
        CountryCode = country,
        IsDefault = isDefault,
    };

    /// <summary>A minimal article with sensible defaults, for index tests.</summary>
    public static KnowledgeArticle Article(
        string id,
        string? title = null,
        string body = "some body text about a fault",
        string category = "power",
        string[]? scope = null,
        string[]? related = null,
        string[]? keywords = null)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in (title + " " + body + " " + string.Join(" ", keywords ?? []))
                     .ToLowerInvariant()
                     .Split([' ', '\n', '\r', '\t', ',', '.', ';', ':', '!', '?'],
                         StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length > 2)
            {
                tokens.Add(token);
            }
        }

        return new KnowledgeArticle
        {
            Id = id,
            Title = title ?? $"Article {id}",
            Category = category,
            Body = body,
            SourcePath = $"knowledge-base/{id}.md",
            ProductScope = scope ?? ["*"],
            Keywords = keywords ?? [],
            Related = related ?? [],
            Tokens = tokens,
        };
    }

    public static KnowledgeBaseLoadResult LoadResult(
        IReadOnlyList<KnowledgeArticle> articles,
        IReadOnlyList<string>? errors = null) => new()
    {
        Articles = articles,
        Errors = errors ?? [],
        LoadedAtUtc = DateTimeOffset.UnixEpoch,
        RootPath = "knowledge-base",
    };
}

/// <summary>
/// Hand-written repository fakes. Kept deliberately dumb: a fake that re-implements
/// production logic would make the tests agree with the code for the wrong reason.
/// </summary>
internal sealed class FakeDeviceRepository : IDeviceRepository
{
    public List<Device> Devices { get; } = [];

    public Dictionary<Guid, bool> OpenRequestByDevice { get; } = [];

    public DeviceLookupResult? LookupResult { get; set; }

    /// <summary>How many times the tool asked for a lookup, so tests can prove it asked
    /// before asserting anything about the answer.</summary>
    public int LookupCallCount { get; private set; }

    public Task<Device?> GetBySerialAsync(string serialNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult(Devices.FirstOrDefault(d => d.SerialNumber == serialNumber));

    public Task<IReadOnlyList<Device>> FindByIdentifierAsync(
        string identifier, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Device>>(
            Devices.Where(d =>
                d.SerialNumber.Equals(identifier, StringComparison.OrdinalIgnoreCase)
                || (d.MacAddress?.Equals(identifier, StringComparison.OrdinalIgnoreCase) ?? false)
                || (d.Imei?.Equals(identifier, StringComparison.OrdinalIgnoreCase) ?? false)).ToList());

    public Task<IReadOnlyList<Device>> SearchAsync(
        DeviceSearchQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Device>>([.. Devices]);

    public Task<DeviceLookupResult> LookupAsync(
        string identifier, DateOnly today, CancellationToken cancellationToken = default)
    {
        LookupCallCount++;
        return Task.FromResult(LookupResult ?? new DeviceLookupResult { MatchScore = 0d });
    }

    public Task<bool> HasOpenRequestAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(OpenRequestByDevice.TryGetValue(deviceId, out var has) && has);
}

internal sealed class FakeCustomerRepository : ICustomerRepository
{
    public List<Address> Addresses { get; } = [];

    public AddressResolution? Resolution { get; set; }

    public Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult<Customer?>(null);

    public Task<IReadOnlyList<Customer>> SearchAsync(
        CustomerSearchQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Customer>>([]);

    public Task<AddressResolution?> ResolveAddressAsync(
        Guid customerId, string? freeTextAddress, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resolution);
}

internal sealed class FakeRmaRepository : IRmaRepository
{
    public List<RmaRequest> Requests { get; } = [];

    public Task AddAsync(RmaRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return Task.CompletedTask;
    }

    public Task<RmaRequest?> GetByNumberAsync(
        string rmaNumber, CancellationToken cancellationToken = default) =>
        Task.FromResult(Requests.FirstOrDefault(r => r.RmaNumber == rmaNumber));

    public Task<RmaRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Requests.FirstOrDefault(r => r.Id == id));

    public Task UpdateAsync(RmaRequest request, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<RmaRequest>> GetForCustomerAsync(
        Guid customerId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RmaRequest>>(
            Requests.Where(r => r.CustomerId == customerId).ToList());

    public Task<IReadOnlyList<RmaRequest>> GetOpenForDeviceAsync(
        Guid deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RmaRequest>>([]);

    public Task<string> NextRmaNumberAsync(DateOnly on, CancellationToken cancellationToken = default) =>
        Task.FromResult($"RMA-{on.Year}-99999");
}

/// <summary>A stub model that returns a canned completion, for pipeline tests.</summary>
internal sealed class StubChatModel : IChatModel
{
    private readonly ChatCompletion? _result;
    private readonly Exception? _throw;

    public StubChatModel(ChatCompletion result) => _result = result;

    public StubChatModel(Exception toThrow) => _throw = toThrow;

    public List<ChatRequest> Requests { get; } = [];

    public string ProviderName => "stub";

    public Task<ChatCompletion> CompleteAsync(
        ChatRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        if (_throw is not null)
        {
            return Task.FromException<ChatCompletion>(_throw);
        }

        // The two constructors are alternatives. If neither branch applies the stub was wired
        // up wrongly, and failing loudly beats returning a fabricated completion.
        return _result is null
            ? Task.FromException<ChatCompletion>(
                new InvalidOperationException("StubChatModel has neither a result nor a throw."))
            : Task.FromResult(_result);
    }
}

/// <summary>A frozen clock, so date-sensitive policy decisions are reproducible.</summary>
internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public DateOnly Today => DateOnly.FromDateTime(UtcNow.UtcDateTime);
}

internal sealed class FakePaymentGateway : IPaymentGateway
{
    public string ProviderName => "fake";

    public bool PaymentRequired { get; set; }

    public Result<PaymentReceipt> AuthorizeResult { get; set; }

    public List<PaymentAuthorizationRequest> Authorizations { get; } = [];

    public List<PaymentRequestContext> RequiredChecks { get; } = [];

    public Task<bool> IsPaymentRequiredAsync(
        PaymentRequestContext context, CancellationToken cancellationToken = default)
    {
        RequiredChecks.Add(context);
        return Task.FromResult(PaymentRequired);
    }

    public Task<Result<PaymentReceipt>> AuthorizeAsync(
        PaymentAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        Authorizations.Add(request);
        return Task.FromResult(AuthorizeResult);
    }

    public Task<Result<PaymentReceipt>> CaptureAsync(
        string authorizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult<Result<PaymentReceipt>>(
            Result<PaymentReceipt>.Success(
                new PaymentReceipt
                {
                    TransactionId = "cap_" + authorizationId,
                    Purpose = PaymentPurpose.Deposit,
                    Amount = default,
                    Status = PaymentStatus.Captured,
                }));

    public Task<Result> VoidAsync(string authorizationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Success());
}

/// <summary>Forces a specific decision, so workflow tests can exercise paths the real policy
/// would need contrived fixtures to reach.</summary>
internal sealed class StubEligibilityService(RmaEligibilityDecision decision) : IEligibilityService
{
    public RmaEligibilityDecision Decision { get; set; } = decision;

    public int CallCount { get; private set; }

    public RmaEligibilityDecision Evaluate(RmaPolicyRequest request)
    {
        CallCount++;
        return Decision;
    }
}
