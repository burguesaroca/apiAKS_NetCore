using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace apiAKS_NetCore.Controllers
{
    [ApiController]
    [Route("cluster")]
    public class MetricsController : ControllerBase
    {
        [HttpGet("cpu")]
        public async Task<IActionResult> GetClusterCpu()
        {
            try
            {
                var kubectlResult = await RunKubectlTopNodes();
                if (kubectlResult != null)
                    return Ok(kubectlResult);
            }
            catch { }

            try
            {
                var inCluster = await QueryInClusterMetricsApi();
                if (inCluster != null)
                    return Ok(inCluster);
            }
            catch (Exception ex)
            {
                return Problem(detail: ex.Message);
            }

            return NotFound("Could not retrieve cluster metrics.");
        }

        [HttpGet("memory")]
        public async Task<IActionResult> GetClusterMemory()
        {
            try
            {
                var kubectlResult = await RunKubectlTopNodesMemory();
                if (kubectlResult != null)
                    return Ok(kubectlResult);
            }
            catch { }

            try
            {
                var inCluster = await QueryInClusterMetricsApiMemory();
                if (inCluster != null)
                    return Ok(inCluster);
            }
            catch (Exception ex)
            {
                return Problem(detail: ex.Message);
            }

            return NotFound("Could not retrieve memory metrics.");
        }

        // --------------------------------------------------------------------
        // MEMORY
        // --------------------------------------------------------------------

        private async Task<object?> RunKubectlTopNodesMemory()
        {
            var psi = new ProcessStartInfo("kubectl")
            {
                Arguments = "top nodes --no-headers",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return null;

            var output = await proc.StandardOutput.ReadToEndAsync();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var nodes = new List<object>();
            double totalMi = 0;
            int count = 0;

            foreach (var line in lines)
            {
                var parts = System.Text.RegularExpressions.Regex.Split(line.Trim(), "\\s+");
                if (parts.Length < 5)
                    continue;

                var name = parts[0];
                var memoryStr = parts[3];
                var memoryPercentStr = parts[4];

                var memoryMi = ParseMemoryValue(memoryStr);
                var memoryPercent = ParsePercent(memoryPercentStr);

                nodes.Add(new { name, memoryMi, memoryPercent });

                totalMi += memoryMi;
                count++;
            }

            double totalCapacityMi = await GetTotalMemoryCapacityFromKubectl();
            double clusterPercent = totalCapacityMi > 0 ? (totalMi / totalCapacityMi) * 100.0 : 0;

            return new
            {
                source = "kubectl",
                nodeCount = count,
                totalMemoryMi = Math.Round(totalMi, 2),
                totalMemoryMiCapacity = Math.Round(totalCapacityMi, 2),
                clusterMemoryPercent = Math.Round(clusterPercent, 2),
                nodes
            };
        }

        private async Task<double> GetTotalMemoryCapacityFromKubectl()
        {
            try
            {
                var psi = new ProcessStartInfo("kubectl")
                {
                    Arguments = "get nodes -o json",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };

                using var proc = Process.Start(psi);
                var output = await proc.StandardOutput.ReadToEndAsync();
                proc.WaitForExit(5000);

                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                    return 0;

                using var doc = JsonDocument.Parse(output);
                var items = doc.RootElement.GetProperty("items");

                double totalCapacityMi = 0;

                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("status", out var status) &&
                        status.TryGetProperty("capacity", out var cap) &&
                        cap.TryGetProperty("memory", out var memCap))
                    {
                        totalCapacityMi += ParseMemoryValue(memCap.GetString() ?? "");
                    }
                }

                return totalCapacityMi;
            }
            catch
            {
                return 0;
            }
        }

        private double ParseMemoryValue(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return 0;

            var lower = s.ToLowerInvariant();

            try
            {
                if (lower.EndsWith("ki") && double.TryParse(lower[..^2], out var vKi))
                    return vKi / 1024.0;

                if (lower.EndsWith("mi") && double.TryParse(lower[..^2], out var vMi))
                    return vMi;

                if (lower.EndsWith("gi") && double.TryParse(lower[..^2], out var vGi))
                    return vGi * 1024.0;

                if (double.TryParse(lower, out var bytes))
                    return bytes / (1024.0 * 1024.0);
            }
            catch { }

            return 0;
        }

        private async Task<object?> QueryInClusterMetricsApiMemory()
        {
            var tokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
            if (!System.IO.File.Exists(tokenPath))
                return null;

            var token = await System.IO.File.ReadAllTextAsync(tokenPath);

            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://kubernetes.default.svc")
            };

            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());

            var resp = await client.GetAsync("/apis/metrics.k8s.io/v1beta1/nodes");
            if (!resp.IsSuccessStatusCode)
                return null;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            var items = doc.RootElement.GetProperty("items");

            var nodes = new List<object>();
            double totalMi = 0;
            int count = 0;

            double totalCapacityMi = await GetTotalMemoryCapacityFromClusterApi(client);

            foreach (var item in items.EnumerateArray())
            {
                var name = item.GetProperty("metadata").GetProperty("name").GetString() ?? "";
                var memStr = item.GetProperty("usage").GetProperty("memory").GetString() ?? "";
                var memMi = ParseMemoryValue(memStr);

                double memoryPercent = totalCapacityMi > 0
                    ? (memMi / (totalCapacityMi / items.GetArrayLength())) * 100.0
                    : 0;

                nodes.Add(new
                {
                    name,
                    memoryMi = memMi,
                    memoryPercent = Math.Round(memoryPercent, 2)
                });

                totalMi += memMi;
                count++;
            }

            double clusterPercent = totalCapacityMi > 0 ? (totalMi / totalCapacityMi) * 100.0 : 0;

            return new
            {
                source = "metrics-api",
                nodeCount = count,
                totalMemoryMi = Math.Round(totalMi, 2),
                totalMemoryMiCapacity = Math.Round(totalCapacityMi, 2),
                clusterMemoryPercent = Math.Round(clusterPercent, 2),
                nodes
            };
        }

        private async Task<double> GetTotalMemoryCapacityFromClusterApi(HttpClient client)
        {
            try
            {
                var resp = await client.GetAsync("/api/v1/nodes");
                if (!resp.IsSuccessStatusCode)
                    return 0;

                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
                var items = doc.RootElement.GetProperty("items");

                double totalCapacityMi = 0;

                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("status", out var status) &&
                        status.TryGetProperty("capacity", out var cap) &&
                        cap.TryGetProperty("memory", out var memCap))
                    {
                        totalCapacityMi += ParseMemoryValue(memCap.GetString() ?? "");
                    }
                }

                return totalCapacityMi;
            }
            catch { return 0; }
        }

        // --------------------------------------------------------------------
        // CPU (CORREGIDO)
        // --------------------------------------------------------------------

        private async Task<object?> RunKubectlTopNodes()
        {
            var psi = new ProcessStartInfo("kubectl")
            {
                Arguments = "top nodes --no-headers",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            using var proc = Process.Start(psi);
            var output = await proc.StandardOutput.ReadToEndAsync();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            var nodes = new List<object>();
            double totalCores = 0;
            int count = 0;

            foreach (var line in lines)
            {
                var p = System.Text.RegularExpressions.Regex.Split(line.Trim(), "\\s+");
                if (p.Length < 3)
                    continue;

                var name = p[0];
                var cpuCores = ParseCpuValue(p[1]);
                var cpuPercent = ParsePercent(p[2]);

                nodes.Add(new { name, cpuCores, cpuPercent });

                totalCores += cpuCores;
                count++;
            }

            double totalCapacity = await GetTotalCpuCapacityFromKubectl();
            double clusterPercent = totalCapacity > 0 ? (totalCores / totalCapacity) * 100.0 : 0;

            return new
            {
                source = "kubectl",
                nodeCount = count,
                totalCpuCoresUsed = Math.Round(totalCores, 4),
                totalCpuCoresCapacity = Math.Round(totalCapacity, 4),
                clusterCpuPercent = Math.Round(clusterPercent, 2),
                nodes
            };
        }

        private async Task<double> GetTotalCpuCapacityFromKubectl()
        {
            try
            {
                var psi = new ProcessStartInfo("kubectl")
                {
                    Arguments = "get nodes -o json",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                };

                using var proc = Process.Start(psi);
                var output = await proc.StandardOutput.ReadToEndAsync();
                proc.WaitForExit(5000);

                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                    return 0;

                using var doc = JsonDocument.Parse(output);
                var items = doc.RootElement.GetProperty("items");

                double total = 0;

                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("status", out var status) &&
                        status.TryGetProperty("capacity", out var cap) &&
                        cap.TryGetProperty("cpu", out var cpuCap))
                    {
                        total += ParseCpuCapacity(cpuCap.GetString() ?? "");
                    }
                }

                return total;
            }
            catch { return 0; }
        }

        // ⭐ NUEVA FUNCIÓN CORRECTA PARA CPU ⭐
        private double ParseCpuValue(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return 0;

            s = s.Trim().ToLowerInvariant();

            // nanoCores → cores
            if (s.EndsWith("n") && double.TryParse(s[..^1], out var n))
                return n / 1_000_000_000.0;

            // milliCores → cores
            if (s.EndsWith("m") && double.TryParse(s[..^1], out var m))
                return m / 1000.0;

            // cores
            if (double.TryParse(s, out var v))
                return v;

            return 0;
        }

        private double ParseCpuCapacity(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return 0;

            s = s.Trim().ToLowerInvariant();

            if (s.EndsWith("m") && double.TryParse(s[..^1], out var m))
                return m / 1000.0;

            if (double.TryParse(s, out var v))
                return v;

            return 0;
        }

        private double ParsePercent(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return 0;

            s = s.Trim().TrimEnd('%');
            return double.TryParse(s, out var v) ? v : 0;
        }

        private async Task<object?> QueryInClusterMetricsApi()
        {
            var tokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
            if (!System.IO.File.Exists(tokenPath))
                return null;

            var token = await System.IO.File.ReadAllTextAsync(tokenPath);

            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://kubernetes.default.svc")
            };

            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());

            var resp = await client.GetAsync("/apis/metrics.k8s.io/v1beta1/nodes");
            if (!resp.IsSuccessStatusCode)
                return null;

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
            var items = doc.RootElement.GetProperty("items");

            var nodes = new List<object>();
            double total = 0;
            int count = 0;

            var capacity = await GetTotalCpuCapacityFromClusterApi(client);

            foreach (var item in items.EnumerateArray())
            {
                var name = item.GetProperty("metadata").GetProperty("name").GetString() ?? "";
                var cpuStr = item.GetProperty("usage").GetProperty("cpu").GetString() ?? "";

                var cpuCores = ParseCpuValue(cpuStr);

                double cpuPercent = capacity > 0
                    ? (cpuCores / (capacity / items.GetArrayLength())) * 100.0
                    : 0;

                nodes.Add(new { name, cpuCores, cpuPercent = Math.Round(cpuPercent, 4) });

                total += cpuCores;
                count++;
            }

            double clusterPercent = capacity > 0 ? (total / capacity) * 100 : 0;

            return new
            {
                source = "metrics-api",
                nodeCount = count,
                totalCpuCoresUsed = Math.Round(total, 4),
                totalCpuCoresCapacity = Math.Round(capacity, 4),
                clusterCpuPercent = Math.Round(clusterPercent, 2),
                nodes
            };
        }

        private async Task<double> GetTotalCpuCapacityFromClusterApi(HttpClient client)
        {
            try
            {
                var resp = await client.GetAsync("/api/v1/nodes");
                if (!resp.IsSuccessStatusCode)
                    return 0;

                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
                var items = doc.RootElement.GetProperty("items");

                double total = 0;

                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("status", out var status) &&
                        status.TryGetProperty("capacity", out var cap) &&
                        cap.TryGetProperty("cpu", out var cpuCap))
                    {
                        total += ParseCpuCapacity(cpuCap.GetString() ?? "");
                    }
                }

                return total;
            }
            catch { return 0; }
        }
    }
}
