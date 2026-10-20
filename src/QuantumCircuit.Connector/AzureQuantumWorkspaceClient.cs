namespace QuantumCircuit.Connector
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Azure.Core;
    using Azure.Identity;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    public sealed class AzureQuantumWorkspaceClient
    {
        private const string ApiVersion = "2026-01-15-preview";
        private const string TokenScope = "https://quantum.microsoft.com/.default";
        private const int MaxQubits = 20;
        private const int MaxGates = 256;
        private const int MaxShots = 1000;
        private const int MaxJobTimeoutSeconds = 180;
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
        private static readonly string[] TerminalFailureStatuses = { "Failed", "Cancelled" };
        private readonly TokenCredential credential;
        private readonly HttpClient httpClient;

        public AzureQuantumWorkspaceClient()
            : this(new DefaultAzureCredential(), new HttpClient { Timeout = TimeSpan.FromSeconds(210) })
        {
        }

        internal AzureQuantumWorkspaceClient(TokenCredential credential, HttpClient httpClient)
        {
            this.credential = credential ?? throw new ArgumentNullException(nameof(credential));
            this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public async Task<JObject> RunCircuitAsync(JObject circuitRequest, CancellationToken cancellationToken = default)
        {
            if (circuitRequest == null)
            {
                throw new ArgumentNullException(nameof(circuitRequest));
            }

            var settings = WorkspaceSettings.Load();
            var program = QuantumProgram.Create(circuitRequest);
            var jobId = Guid.NewGuid().ToString("D");
            var containerUri = await GetSasUriAsync(settings, jobId, null, cancellationToken).ConfigureAwait(false);
            await UploadInputAsync(containerUri, program.InputData, program.ContentType, cancellationToken).ConfigureAwait(false);

            var inputUri = AppendBlob(containerUri, "inputData");
            var jobRequest = new JObject
            {
                ["name"] = "logic-app-quantum-circuit",
                ["providerId"] = program.ProviderId,
                ["target"] = settings.TargetId,
                ["itemType"] = "Job",
                ["containerUri"] = containerUri.AbsoluteUri,
                ["inputDataUri"] = inputUri.AbsoluteUri,
                ["inputDataFormat"] = program.InputDataFormat,
                ["outputDataFormat"] = program.OutputDataFormat,
                ["inputParams"] = program.InputParameters,
            };

            var jobUri = settings.WorkspaceResourceId
                + "/jobs/" + Uri.EscapeDataString(jobId)
                + "?api-version=" + ApiVersion;
            await SendWorkspaceRequestAsync(
                settings,
                HttpMethod.Put,
                jobUri,
                jobRequest,
                cancellationToken).ConfigureAwait(false);

            var job = await WaitForJobAsync(settings, jobUri, cancellationToken).ConfigureAwait(false);
            var resultData = await DownloadOutputAsync(settings, job, cancellationToken).ConfigureAwait(false);
            var counts = NormalizeResults(settings.Provider, resultData, program.QubitCount, program.Shots);
            var completedShots = counts.Values.Sum();
            if (completedShots <= 0)
            {
                throw new InvalidOperationException("Azure Quantum returned no measurement counts.");
            }

            var probabilities = new JObject();
            foreach (var count in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                probabilities[count.Key] = (double)count.Value / completedShots;
            }

            var sample = counts.OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .First().Key;

            return new JObject
            {
                ["backend"] = "AzureQuantum",
                ["provider"] = settings.Provider,
                ["targetId"] = settings.TargetId,
                ["jobId"] = jobId,
                ["status"] = "Succeeded",
                ["qubitCount"] = program.QubitCount,
                ["gateCount"] = program.GateCount,
                ["shots"] = completedShots,
                ["requestedShots"] = program.Shots,
                ["sample"] = sample,
                ["counts"] = JObject.FromObject(counts),
                ["probabilities"] = probabilities,
            };
        }

        private async Task<Uri> GetSasUriAsync(
            WorkspaceSettings settings,
            string containerName,
            string blobName,
            CancellationToken cancellationToken)
        {
            var blobDetails = new JObject { ["containerName"] = containerName };
            if (!string.IsNullOrEmpty(blobName))
            {
                blobDetails["blobName"] = blobName;
            }

            var response = await SendWorkspaceRequestAsync(
                settings,
                HttpMethod.Post,
                settings.WorkspaceResourceId + "/storage/sasUri?api-version=" + ApiVersion,
                blobDetails,
                cancellationToken).ConfigureAwait(false);
            var sas = response?["sasUri"]?.Value<string>();
            if (!Uri.TryCreate(sas, UriKind.Absolute, out var sasUri)
                || sasUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new AzureQuantumApiException("Azure Quantum returned an invalid storage SAS URI.");
            }

            return sasUri;
        }

        private async Task UploadInputAsync(
            Uri containerUri,
            string inputData,
            string contentType,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, AppendBlob(containerUri, "inputData"))
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(inputData)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
            request.Headers.TryAddWithoutValidation("x-ms-version", "2023-11-03");
            using var response = await this.httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "uploading the Azure Quantum job input").ConfigureAwait(false);
        }

        private async Task<JObject> WaitForJobAsync(
            WorkspaceSettings settings,
            string jobUri,
            CancellationToken cancellationToken)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.JobTimeoutSeconds);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                var job = await SendWorkspaceRequestAsync(
                    settings,
                    HttpMethod.Get,
                    jobUri,
                    null,
                    cancellationToken).ConfigureAwait(false);
                var status = job?["status"]?.Value<string>();
                if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
                {
                    return job;
                }

                if (TerminalFailureStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
                {
                    var detail = job?["errorData"]?.ToString(Formatting.None);
                    throw new AzureQuantumApiException(
                        $"Azure Quantum job ended with status '{status}'"
                        + (string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}"));
                }
            }

            throw new TimeoutException(
                $"Azure Quantum job did not finish within {settings.JobTimeoutSeconds} seconds.");
        }

        private async Task<JToken> DownloadOutputAsync(
            WorkspaceSettings settings,
            JObject job,
            CancellationToken cancellationToken)
        {
            var outputUri = job["outputDataUri"]?.Value<string>();
            if (!Uri.TryCreate(outputUri, UriKind.Absolute, out var parsedOutputUri)
                || parsedOutputUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new AzureQuantumApiException(
                    "The completed Azure Quantum job has no valid HTTPS outputDataUri.");
            }

            Uri downloadUri;
            if (!string.IsNullOrEmpty(parsedOutputUri.Query))
            {
                downloadUri = parsedOutputUri;
            }
            else
            {
                var pathSegments = parsedOutputUri.AbsolutePath
                    .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (pathSegments.Length < 2)
                {
                    throw new AzureQuantumApiException("Azure Quantum returned an invalid output blob URI.");
                }

                var sasUri = await GetSasUriAsync(
                    settings,
                    Uri.UnescapeDataString(pathSegments[pathSegments.Length - 2]),
                    Uri.UnescapeDataString(pathSegments[pathSegments.Length - 1]),
                    cancellationToken).ConfigureAwait(false);
                downloadUri = sasUri;
            }

            using var response = await this.httpClient.GetAsync(downloadUri, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, "downloading the Azure Quantum job output").ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return JToken.Parse(text);
            }
            catch (JsonReaderException ex)
            {
                throw new AzureQuantumApiException("Azure Quantum returned invalid JSON job results.", ex);
            }
        }

        private async Task<JObject> SendWorkspaceRequestAsync(
            WorkspaceSettings settings,
            HttpMethod method,
            string relativeUri,
            JObject body,
            CancellationToken cancellationToken)
        {
            var uri = relativeUri.StartsWith("/", StringComparison.Ordinal)
                ? new Uri(settings.Endpoint, relativeUri)
                : new Uri(settings.Endpoint, relativeUri);
            var token = await this.credential.GetTokenAsync(
                new TokenRequestContext(new[] { TokenScope }),
                cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            if (body != null)
            {
                request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            }

            using var response = await this.httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new AzureQuantumApiException(
                    $"Azure Quantum returned HTTP {(int)response.StatusCode} "
                    + $"while {DescribeRequest(method, relativeUri)}: {Limit(text, 1000)}",
                    response.StatusCode);
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return new JObject();
            }

            try
            {
                return JObject.Parse(text);
            }
            catch (JsonReaderException ex)
            {
                throw new AzureQuantumApiException("Azure Quantum returned an invalid JSON response.", ex);
            }
        }

        private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
        {
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new AzureQuantumApiException(
                $"HTTP {(int)response.StatusCode} while {operation}: {Limit(text, 1000)}",
                response.StatusCode);
        }

        private static string DescribeRequest(HttpMethod method, string relativeUri) =>
            $"{method.Method} {relativeUri.Split('?')[0]}";

        private static string Limit(string value, int maxLength) =>
            value.Length <= maxLength ? value : value.Substring(0, maxLength);

        private static Uri AppendBlob(Uri containerUri, string blobName)
        {
            var builder = new UriBuilder(containerUri)
            {
                Path = containerUri.AbsolutePath.TrimEnd('/') + "/" + Uri.EscapeDataString(blobName),
            };
            return builder.Uri;
        }

        private static Dictionary<string, int> NormalizeResults(
            string provider,
            JToken result,
            int qubitCount,
            int requestedShots)
        {
            switch (provider)
            {
                case "rigetti":
                    return NormalizeRigetti(result, qubitCount);
                case "ionq":
                    return NormalizeIonQ(result, qubitCount, requestedShots);
                case "quantinuum":
                    return NormalizeQuantinuum(result, qubitCount);
                default:
                    throw new InvalidOperationException($"Unsupported Azure Quantum provider '{provider}'.");
            }
        }

        private static Dictionary<string, int> NormalizeRigetti(JToken result, int qubitCount)
        {
            var readouts = result["ro"] as JArray;
            if (readouts == null || readouts.Count == 0)
            {
                throw new InvalidOperationException("Azure Quantum returned no Rigetti 'ro' measurements.");
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var readout in readouts)
            {
                if (readout is not JArray bits || bits.Count != qubitCount)
                {
                    throw new InvalidOperationException(
                        $"Azure Quantum returned a Rigetti measurement with width "
                        + $"{(readout as JArray)?.Count ?? 0}; expected {qubitCount}.");
                }

                var state = new StringBuilder(qubitCount);
                foreach (var bit in bits)
                {
                    if (bit.Type != JTokenType.Integer || (bit.Value<int>() != 0 && bit.Value<int>() != 1))
                    {
                        throw new InvalidOperationException("Azure Quantum returned an invalid Rigetti measurement bit.");
                    }

                    state.Append(bit.Value<int>());
                }

                AddCount(counts, state.ToString());
            }

            return counts;
        }

        private static Dictionary<string, int> NormalizeIonQ(JToken result, int qubitCount, int requestedShots)
        {
            var histogram = result["histogram"] as JObject;
            if (histogram == null || !histogram.Properties().Any())
            {
                throw new InvalidOperationException("Azure Quantum returned no IonQ measurement histogram.");
            }

            var weights = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var property in histogram.Properties())
            {
                if (!int.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var outcome)
                    || outcome < 0
                    || outcome >= (1 << qubitCount)
                    || property.Value.Type != JTokenType.Integer && property.Value.Type != JTokenType.Float)
                {
                    throw new InvalidOperationException("Azure Quantum returned an invalid IonQ histogram entry.");
                }

                var weight = property.Value.Value<double>();
                if (!double.IsFinite(weight) || weight < 0)
                {
                    throw new InvalidOperationException("Azure Quantum returned an invalid IonQ histogram weight.");
                }

                var state = Convert.ToString(outcome, 2).PadLeft(qubitCount, '0').Reverse().ToArray();
                var bitString = new string(state);
                weights[bitString] = weights.TryGetValue(bitString, out var existing) ? existing + weight : weight;
            }

            var totalWeight = weights.Values.Sum();
            if (!double.IsFinite(totalWeight) || totalWeight <= 0)
            {
                throw new InvalidOperationException("Azure Quantum returned an empty IonQ measurement histogram.");
            }

            if (totalWeight <= 1.0)
            {
                foreach (var key in weights.Keys.ToArray())
                {
                    weights[key] *= requestedShots;
                }

                totalWeight = requestedShots;
            }

            var targetShots = (int)Math.Floor(totalWeight + 0.5);
            var counts = weights.ToDictionary(
                pair => pair.Key,
                pair => (int)Math.Floor(pair.Value),
                StringComparer.Ordinal);
            var remaining = targetShots - counts.Values.Sum();
            foreach (var item in weights
                .Select(pair => new { pair.Key, Remainder = pair.Value - counts[pair.Key] })
                .OrderByDescending(item => item.Remainder)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Take(Math.Max(0, remaining)))
            {
                counts[item.Key]++;
            }

            return counts.Where(pair => pair.Value > 0).ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
        }

        private static Dictionary<string, int> NormalizeQuantinuum(JToken result, int qubitCount)
        {
            var readouts = result["ro"];
            if (readouts is JValue single && single.Type == JTokenType.String)
            {
                readouts = new JArray(single.Value<string>());
            }

            if (readouts is not JArray array || array.Count == 0)
            {
                throw new InvalidOperationException("Azure Quantum returned no Quantinuum 'ro' measurements.");
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var readout in array)
            {
                var bits = readout.Value<string>();
                if (bits == null || bits.Length != qubitCount || bits.Any(bit => bit != '0' && bit != '1'))
                {
                    throw new InvalidOperationException("Azure Quantum returned an invalid Quantinuum measurement bit string.");
                }

                AddCount(counts, new string(bits.Reverse().ToArray()));
            }

            return counts;
        }

        private static void AddCount(Dictionary<string, int> counts, string state)
        {
            counts[state] = counts.TryGetValue(state, out var count) ? count + 1 : 1;
        }

        private sealed class WorkspaceSettings
        {
            private WorkspaceSettings(Uri endpoint, string workspaceResourceId, string targetId, int jobTimeoutSeconds)
            {
                this.Endpoint = endpoint;
                this.WorkspaceResourceId = workspaceResourceId;
                this.TargetId = targetId;
                this.JobTimeoutSeconds = jobTimeoutSeconds;
                this.Provider = targetId.Split('.')[0].ToLowerInvariant();
            }

            public Uri Endpoint { get; }

            public string WorkspaceResourceId { get; }

            public string TargetId { get; }

            public string Provider { get; }

            public int JobTimeoutSeconds { get; }

            public static WorkspaceSettings Load()
            {
                var endpointValue = Environment.GetEnvironmentVariable("AZURE_QUANTUM_ENDPOINT");
                var workspaceResourceId = Environment.GetEnvironmentVariable("AZURE_QUANTUM_WORKSPACE_RESOURCE_ID");
                var targetId = Environment.GetEnvironmentVariable("AZURE_QUANTUM_TARGET_ID");
                if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint)
                    || endpoint.Scheme != Uri.UriSchemeHttps
                    || string.IsNullOrWhiteSpace(workspaceResourceId)
                    || string.IsNullOrWhiteSpace(targetId))
                {
                    throw new AzureQuantumConfigurationException(
                        "Configure AZURE_QUANTUM_ENDPOINT, AZURE_QUANTUM_WORKSPACE_RESOURCE_ID, "
                        + "and AZURE_QUANTUM_TARGET_ID in the Logic App host settings.");
                }

                if (!workspaceResourceId.StartsWith(
                        "/subscriptions/",
                        StringComparison.OrdinalIgnoreCase)
                    || !workspaceResourceId.Contains(
                        "/providers/Microsoft.Quantum/workspaces/",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new AzureQuantumConfigurationException(
                        "AZURE_QUANTUM_WORKSPACE_RESOURCE_ID must be the full Azure Quantum workspace resource ID.");
                }

                var provider = targetId.Split('.')[0].ToLowerInvariant();
                if (provider != "rigetti" && provider != "ionq" && provider != "quantinuum")
                {
                    throw new AzureQuantumConfigurationException(
                        "AZURE_QUANTUM_TARGET_ID must identify a Rigetti, IonQ, or Quantinuum gate target.");
                }

                var timeoutValue = Environment.GetEnvironmentVariable("AZURE_QUANTUM_JOB_TIMEOUT_SECONDS");
                var timeout = string.IsNullOrWhiteSpace(timeoutValue)
                    ? 180
                    : int.TryParse(timeoutValue, NumberStyles.None, CultureInfo.InvariantCulture, out var configuredTimeout)
                        ? configuredTimeout
                        : 0;
                if (timeout is < 1 or > MaxJobTimeoutSeconds)
                {
                    throw new AzureQuantumConfigurationException(
                        $"AZURE_QUANTUM_JOB_TIMEOUT_SECONDS must be between 1 and {MaxJobTimeoutSeconds}.");
                }

                return new WorkspaceSettings(
                    endpoint,
                    workspaceResourceId.TrimEnd('/'),
                    targetId,
                    timeout);
            }
        }

        private sealed class QuantumProgram
        {
            private QuantumProgram(
                string providerId,
                string inputData,
                string contentType,
                string inputDataFormat,
                string outputDataFormat,
                JObject inputParameters,
                int qubitCount,
                int gateCount,
                int shots)
            {
                this.ProviderId = providerId;
                this.InputData = inputData;
                this.ContentType = contentType;
                this.InputDataFormat = inputDataFormat;
                this.OutputDataFormat = outputDataFormat;
                this.InputParameters = inputParameters;
                this.QubitCount = qubitCount;
                this.GateCount = gateCount;
                this.Shots = shots;
            }

            public string ProviderId { get; }

            public string InputData { get; }

            public string ContentType { get; }

            public string InputDataFormat { get; }

            public string OutputDataFormat { get; }

            public JObject InputParameters { get; }

            public int QubitCount { get; }

            public int GateCount { get; }

            public int Shots { get; }

            public static QuantumProgram Create(JObject request)
            {
                var initialStateToken = request["initialState"];
                var initialState = initialStateToken?.Type == JTokenType.String
                    ? initialStateToken.Value<string>()
                    : null;
                var gateInstructions = request["gates"] as JArray;
                var shotsToken = request["shots"];
                var shots = 100;
                if (shotsToken != null && shotsToken.Type != JTokenType.Null)
                {
                    if (shotsToken.Type != JTokenType.Integer)
                    {
                        throw new ArgumentException($"shots must be an integer between 1 and {MaxShots}.");
                    }

                    shots = shotsToken.Value<int>();
                }
                if (string.IsNullOrEmpty(initialState)
                    || initialState.Length > MaxQubits
                    || initialState.Any(bit => bit != '0' && bit != '1'))
                {
                    throw new ArgumentException($"initialState must contain 1-{MaxQubits} computational-basis bits.");
                }

                if (gateInstructions == null || gateInstructions.Count > MaxGates)
                {
                    throw new ArgumentException($"gates must be an array containing at most {MaxGates} gates.");
                }

                if (shots is < 1 or > MaxShots)
                {
                    throw new ArgumentException($"shots must be an integer between 1 and {MaxShots}.");
                }

                var targetId = Environment.GetEnvironmentVariable("AZURE_QUANTUM_TARGET_ID") ?? string.Empty;
                var provider = targetId.Split('.')[0].ToLowerInvariant();
                var quil = new List<string> { $"DECLARE ro BIT[{initialState.Length}]" };
                var openQasm = new List<string>
                {
                    "OPENQASM 2.0;",
                    "include \"qelib1.inc\";",
                    $"qreg q[{initialState.Length}];",
                    $"creg ro[{initialState.Length}];",
                };
                var ionqGates = new JArray();

                for (var qubit = 0; qubit < initialState.Length; qubit++)
                {
                    if (initialState[qubit] == '1')
                    {
                        quil.Add($"X {qubit}");
                        openQasm.Add($"x q[{qubit}];");
                        ionqGates.Add(new JObject { ["gate"] = "x", ["target"] = qubit });
                    }
                }

                for (var index = 0; index < gateInstructions.Count; index++)
                {
                    if (gateInstructions[index] is not JObject instruction)
                    {
                        throw new ArgumentException($"gates[{index}] must be an object.");
                    }

                    var gate = instruction["gate"]?.Value<string>()?.Trim().ToUpperInvariant();
                    var target = ReadQubit(instruction, "target", initialState.Length, index);
                    switch (gate)
                    {
                        case "H":
                        case "X":
                        case "Y":
                        case "Z":
                        case "S":
                        case "T":
                            quil.Add($"{gate} {target}");
                            openQasm.Add($"{gate.ToLowerInvariant()} q[{target}];");
                            ionqGates.Add(new JObject { ["gate"] = gate.ToLowerInvariant(), ["target"] = target });
                            break;
                        case "S_DAG":
                        case "SDG":
                        case "T_DAG":
                        case "TDG":
                            var isS = gate == "S_DAG" || gate == "SDG";
                            quil.Add($"DAGGER {(isS ? "S" : "T")} {target}");
                            openQasm.Add($"{(isS ? "sdg" : "tdg")} q[{target}];");
                            ionqGates.Add(new JObject
                            {
                                ["gate"] = isS ? "si" : "ti",
                                ["target"] = target,
                            });
                            break;
                        case "CNOT":
                        case "CX":
                        case "CZ":
                        case "SWAP":
                            var control = ReadQubit(instruction, "control", initialState.Length, index);
                            if (control == target)
                            {
                                throw new ArgumentException($"gates[{index}] control and target must differ.");
                            }

                            if (gate == "CNOT" || gate == "CX")
                            {
                                quil.Add($"CNOT {control} {target}");
                                openQasm.Add($"cx q[{control}],q[{target}];");
                                ionqGates.Add(new JObject
                                {
                                    ["gate"] = "cnot",
                                    ["control"] = control,
                                    ["target"] = target,
                                });
                            }
                            else if (gate == "CZ")
                            {
                                quil.Add($"CZ {control} {target}");
                                openQasm.Add($"cz q[{control}],q[{target}];");
                                ionqGates.Add(new JObject
                                {
                                    ["gate"] = "z",
                                    ["control"] = control,
                                    ["target"] = target,
                                });
                            }
                            else
                            {
                                quil.Add($"SWAP {control} {target}");
                                openQasm.Add($"swap q[{control}],q[{target}];");
                                ionqGates.Add(new JObject
                                {
                                    ["gate"] = "swap",
                                    ["targets"] = new JArray(control, target),
                                });
                            }
                            break;
                        case "TOFFOLI":
                        case "CCNOT":
                            var control1 = ReadQubit(instruction, "control", initialState.Length, index);
                            var control2 = ReadQubit(instruction, "control2", initialState.Length, index);
                            if (new[] { control1, control2, target }.Distinct().Count() != 3)
                            {
                                throw new ArgumentException($"gates[{index}] Toffoli qubits must be distinct.");
                            }

                            quil.Add($"CCNOT {control1} {control2} {target}");
                            openQasm.Add($"ccx q[{control1}],q[{control2}],q[{target}];");
                            ionqGates.Add(new JObject
                            {
                                ["gate"] = "not",
                                ["controls"] = new JArray(control1, control2),
                                ["target"] = target,
                            });
                            break;
                        case "RX":
                        case "RY":
                        case "RZ":
                            var angle = instruction["angle"];
                            if (angle?.Type != JTokenType.Integer
                                && angle?.Type != JTokenType.Float)
                            {
                                throw new ArgumentException($"gates[{index}] '{gate}' requires a finite numeric angle.");
                            }

                            var radians = angle.Value<double>();
                            if (!double.IsFinite(radians))
                            {
                                throw new ArgumentException($"gates[{index}] '{gate}' requires a finite numeric angle.");
                            }

                            var formattedAngle = radians.ToString("R", CultureInfo.InvariantCulture);
                            quil.Add($"{gate}({formattedAngle}) {target}");
                            openQasm.Add($"{gate.ToLowerInvariant()}({formattedAngle}) q[{target}];");
                            ionqGates.Add(new JObject
                            {
                                ["gate"] = gate.ToLowerInvariant(),
                                ["rotation"] = radians,
                                ["target"] = target,
                            });
                            break;
                        default:
                            throw new ArgumentException(
                                $"gates[{index}] uses unsupported gate '{gate ?? "(missing)"}'.");
                    }
                }

                for (var qubit = 0; qubit < initialState.Length; qubit++)
                {
                    quil.Add($"MEASURE {qubit} ro[{qubit}]");
                    openQasm.Add($"measure q[{qubit}] -> ro[{qubit}];");
                }

                var inputParameters = new JObject { ["shots"] = shots };
                switch (provider)
                {
                    case "rigetti":
                        return new QuantumProgram(
                            "rigetti",
                            string.Join("\n", quil) + "\n",
                            "text/plain",
                            "rigetti.quil.v1",
                            "rigetti.quil-results.v1",
                            inputParameters,
                            initialState.Length,
                            gateInstructions.Count,
                            shots);
                    case "ionq":
                        return new QuantumProgram(
                            "IonQ",
                            new JObject
                            {
                                ["qubits"] = initialState.Length,
                                ["circuit"] = ionqGates,
                            }.ToString(Formatting.None),
                            "application/json",
                            "ionq.circuit.v1",
                            "ionq.quantum-results.v1",
                            inputParameters,
                            initialState.Length,
                            gateInstructions.Count,
                            shots);
                    case "quantinuum":
                        return new QuantumProgram(
                            "quantinuum",
                            string.Join("\n", openQasm) + "\n",
                            "application/qasm",
                            "honeywell.openqasm.v1",
                            "honeywell.quantum-results.v1",
                            inputParameters,
                            initialState.Length,
                            gateInstructions.Count,
                            shots);
                    default:
                        throw new InvalidOperationException(
                            "The configured target must be a Rigetti, IonQ, or Quantinuum gate target.");
                }
            }

            private static int ReadQubit(JObject instruction, string name, int qubitCount, int gateIndex)
            {
                var value = instruction[name];
                if (value?.Type != JTokenType.Integer)
                {
                    throw new ArgumentException(
                        $"gates[{gateIndex}] '{name}' must be an integer qubit index.");
                }

                var qubit = value.Value<int>();
                if (qubit < 0 || qubit >= qubitCount)
                {
                    throw new ArgumentException(
                        $"gates[{gateIndex}] '{name}' must be a qubit index in [0, {qubitCount - 1}].");
                }

                return qubit;
            }
        }
    }

    public sealed class AzureQuantumApiException : Exception
    {
        public AzureQuantumApiException(string message)
            : base(message)
        {
        }

        public AzureQuantumApiException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public AzureQuantumApiException(string message, HttpStatusCode statusCode)
            : base(message)
        {
            this.StatusCode = statusCode;
        }

        public HttpStatusCode? StatusCode { get; }
    }

    public sealed class AzureQuantumConfigurationException : InvalidOperationException
    {
        public AzureQuantumConfigurationException(string message)
            : base(message)
        {
        }
    }
}
