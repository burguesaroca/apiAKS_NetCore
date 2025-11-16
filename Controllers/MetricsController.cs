using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            // Try kubectl first (works if kubectl is installed and configured locally)
            try
            {
                var kubectlResult = await RunKubectlTopNodes();
                if (kubectlResult != null)
                    return Ok(kubectlResult);
            }
            catch
            {
                // fallthrough to in-cluster method
            }

            // Try in-cluster metrics API
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

            return NotFound("Could not retrieve cluster metrics via kubectl or in-cluster metrics API.");
        }

        [HttpGet("memory")]
        public async Task<IActionResult> GetClusterMemory()
        {
            // Try kubectl first
            try
            {
                var kubectlResult = await RunKubectlTopNodesMemory();
                if (kubectlResult != null)
                    return Ok(kubectlResult);
            }
            catch
            {
                // fallthrough
            }

            // Try in-cluster metrics API
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

            return NotFound("Could not retrieve cluster memory metrics via kubectl or in-cluster metrics API.");
        }

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
            var err = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var nodes = new List<object>();
            double totalMi = 0;
            double totalPercent = 0;
            int count = 0;

            foreach (var line in lines)
            {
                var parts = System.Text.RegularExpressions.Regex.Split(line.Trim(), "\\s+");
                if (parts.Length < 5)
                    continue;

                var name = parts[0];
                var memoryStr = parts[3]; // e.g. 1024Mi or 1Gi
                var memoryPercentStr = parts.Length > 4 ? parts[4] : ""; // e.g. 10%

                var memoryMi = ParseMemoryValue(memoryStr);
                var memoryPercent = ParsePercent(memoryPercentStr);

                nodes.Add(new { name, memoryMi, memoryPercent });
                totalMi += memoryMi;
                totalPercent += memoryPercent;
                count++;
            }

            var avgPercent = count > 0 ? totalPercent / count : 0;

            // Capacity via kubectl get nodes -o json
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
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                    return 0;

                var output = await proc.StandardOutput.ReadToEndAsync();
                proc.WaitForExit(5000);
                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                    return 0;

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;
                if (!root.TryGetProperty("items", out var items))
                    return 0;

                double totalCapacityMi = 0;
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("status", out var status) && status.TryGetProperty("capacity", out var cap))
                    {
                        if (cap.TryGetProperty("memory", out var memCap))
                        {
                            var memStr = memCap.GetString() ?? "";
                            totalCapacityMi += ParseMemoryValue(memStr);
                        }
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
            s = s.Trim();
            // handle Ki, Mi, Gi (case-insensitive)
            var lower = s.ToLowerInvariant();
            try
            {
                if (lower.EndsWith("ki"))
                {
                    if (double.TryParse(lower[..^2], out var v))
                        return v / 1024.0; // Ki -> Mi
                }
                else if (lower.EndsWith("mi"))
                {
                    if (double.TryParse(lower[..^2], out var v))
                        return v; // Mi
                }
                else if (lower.EndsWith("gi"))
                {
                    if (double.TryParse(lower[..^2], out var v))
                        return v * 1024.0; // Gi -> Mi
                }
                else if (lower.EndsWith("k"))
                {
                    if (double.TryParse(lower[..^1], out var v))
                        return v / 1024.0;
                }
                else if (lower.EndsWith("m"))
                {
                    if (double.TryParse(lower[..^1], out var v))
                        return v;
                }
                else if (double.TryParse(lower, out var bytes))
                {
                    // assume bytes
                    return bytes / (1024.0 * 1024.0);
                }
            }
            catch
            {
                return 0;
            }
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
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());

            var resp = await client.GetAsync("/apis/metrics.k8s.io/v1beta1/nodes");
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var root = doc.RootElement;
            if (!root.TryGetProperty("items", out var items))
                return null;

            var nodes = new List<object>();
            double totalMi = 0;
            int count = 0;

            foreach (var item in items.EnumerateArray())
            {
                var name = item.GetProperty("metadata").GetProperty("name").GetString() ?? "";
                var usage = item.GetProperty("usage");
                var memStr = usage.GetProperty("memory").GetString() ?? ""; // e.g. 123456Ki or 123Mi
                var memMi = ParseMemoryValue(memStr);
                nodes.Add(new { name, memMi });
                totalMi += memMi;
                count++;
            }

            // Get capacity via cluster API
            double totalCapacityMi = await GetTotalMemoryCapacityFromClusterApi(client);
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
                var respNodes = await client.GetAsync("/api/v1/nodes");
                if (!respNodes.IsSuccessStatusCode)
                    return 0;

                using var stream = await respNodes.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;
                if (!root.TryGetProperty("items", out var items))
                    return 0;

                double totalCapacityMi = 0;
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("status", out var status) && status.TryGetProperty("capacity", out var cap))
                    {
                        if (cap.TryGetProperty("memory", out var memCap))
                        {
                            var memStr = memCap.GetString() ?? "";
                            totalCapacityMi += ParseMemoryValue(memStr);
                        }
                    }
                }

                return totalCapacityMi;
            }
            catch
            {
                return 0;
            }
        }

        private async Task<object?> RunKubectlTopNodes()
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
            var err = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var nodes = new List<object>();
            double totalCores = 0;
            double totalPercent = 0;
            int count = 0;

            foreach (var line in lines)
            {
                var parts = System.Text.RegularExpressions.Regex.Split(line.Trim(), "\\s+");
                if (parts.Length < 3)
                    continue;

                var name = parts[0];
                var cpuCoresStr = parts[1]; // e.g. 250m or 0.25
                var cpuPercentStr = parts[2]; // e.g. 10%

                var cpuCores = ParseCpuValue(cpuCoresStr);
                var cpuPercent = ParsePercent(cpuPercentStr);

                nodes.Add(new { name, cpuCores, cpuPercent });
                totalCores += cpuCores;
                totalPercent += cpuPercent;
                count++;
            }

            // Try to get capacity using `kubectl get nodes -o json`
            double totalCapacity = await GetTotalCpuCapacityFromKubectl();
            double clusterPercent = totalCapacity > 0 ? (totalCores / totalCapacity) * 100.0 : 0;

            return new
            {
                source = "kubectl",
                nodeCount = count,
                totalCpuCoresUsed = Math.Round(totalCores, 3),
                totalCpuCoresCapacity = Math.Round(totalCapacity, 3),
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
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };

                using var proc = Process.Start(psi);
                if (proc == null)
                    return 0;

                var output = await proc.StandardOutput.ReadToEndAsync();
                proc.WaitForExit(5000);
                if (proc.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                    return 0;

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;
                if (!root.TryGetProperty("items", out var items))
                    return 0;

                double totalCapacity = 0;
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("status", out var status) && status.TryGetProperty("capacity", out var cap))
                    {
                        if (cap.TryGetProperty("cpu", out var cpuCap))
                        {
                            var cpuStr = cpuCap.GetString() ?? "";
                            totalCapacity += ParseCpuCapacity(cpuStr);
                        }
                    }
                }

                return totalCapacity;
            }
            catch
            {
                return 0;
            }
        }

        private double ParseCpuValue(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return 0;

            s = s.Trim();
            if (s.EndsWith("m"))
            {
                if (double.TryParse(s[..^1], out var milli))
                    return milli / 1000.0;
            }
            else
            {
                if (double.TryParse(s, out var val))
                    return val;
            }

            return 0;
        }

        private double ParseCpuCapacity(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return 0;
            var v = s.Trim();
            // capacity usually in cores like "4", but may be in millicores
            if (v.EndsWith("m"))
            {
                if (double.TryParse(v[..^1], out var milli))
                    return milli / 1000.0;
            }
            else
            {
                if (double.TryParse(v, out var cores))
                    return cores;
            }
            return 0;
        }

        private double ParsePercent(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return 0;
            s = s.Trim().TrimEnd('%');
            if (double.TryParse(s, out var v))
                return v;
            return 0;
        }

        private async Task<object?> QueryInClusterMetricsApi()
        {
            // In-cluster service account token and CA
            var tokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";
            var caPath = "/var/run/secrets/kubernetes.io/serviceaccount/ca.crt";

            if (!System.IO.File.Exists(tokenPath))
                return null;

            var token = await System.IO.File.ReadAllTextAsync(tokenPath);

            var handler = new HttpClientHandler();
            // Accept default cert validation but allow self-signed using CA isn't implemented here.
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

            using var client = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://kubernetes.default.svc")
            };
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Trim());

            var resp = await client.GetAsync("/apis/metrics.k8s.io/v1beta1/nodes");
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);

            var root = doc.RootElement;
            if (!root.TryGetProperty("items", out var items))
                return null;

            var nodes = new List<object>();
            double totalCores = 0;
            int count = 0;

            foreach (var item in items.EnumerateArray())
            {
                var name = item.GetProperty("metadata").GetProperty("name").GetString() ?? "";
                var usage = item.GetProperty("usage");
                var cpuStr = usage.GetProperty("cpu").GetString() ?? ""; // e.g. 250m

                var cpuCores = ParseCpuValue(cpuStr);
                nodes.Add(new { name, cpuCores });
                totalCores += cpuCores;
                count++;
            }

            // Get capacities via API /api/v1/nodes
            double totalCapacity = await GetTotalCpuCapacityFromClusterApi(client);
            double clusterPercent = totalCapacity > 0 ? (totalCores / totalCapacity) * 100.0 : 0;

            return new
            {
                source = "metrics-api",
                nodeCount = count,
                totalCpuCoresUsed = Math.Round(totalCores, 3),
                totalCpuCoresCapacity = Math.Round(totalCapacity, 3),
                clusterCpuPercent = Math.Round(clusterPercent, 2),
                nodes
            };
        }

        private async Task<double> GetTotalCpuCapacityFromClusterApi(HttpClient client)
        {
            try
            {
                var respNodes = await client.GetAsync("/api/v1/nodes");
                if (!respNodes.IsSuccessStatusCode)
                    return 0;

                using var stream = await respNodes.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;
                if (!root.TryGetProperty("items", out var items))
                    return 0;

                double totalCapacity = 0;
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("status", out var status) && status.TryGetProperty("capacity", out var cap))
                    {
                        if (cap.TryGetProperty("cpu", out var cpuCap))
                        {
                            var cpuStr = cpuCap.GetString() ?? "";
                            totalCapacity += ParseCpuCapacity(cpuStr);
                        }
                    }
                }

                return totalCapacity;
            }
            catch
            {
                return 0;
            }
        }
    }
}
