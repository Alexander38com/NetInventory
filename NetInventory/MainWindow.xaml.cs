using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.DirectoryServices;
using System.Threading.Tasks;
using System.Windows;
using System.Collections.Concurrent;
using System.Threading;

namespace NetInventory
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        public class PcInfo
        {
            public string ComputerName { get; set; }
            public string IPAddress { get; set; }
            public string UserName { get; set; }
            public string Model { get; set; }
            public string CPU { get; set; }
            public string OS { get; set; }
            public string RAM_GB { get; set; }
            public string Disks { get; set; } // НОВОЕ: Информация о дисках
        }

        private async void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            btnScan.IsEnabled = false;
            btnScan.Content = "Сканирую...";
            progressBar.Visibility = Visibility.Visible;
            progressBar.Value = 0;

            var pcs = txtComputers.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (pcs.Count == 0)
            {
                btnScan.IsEnabled = true;
                btnScan.Content = "Сканировать";
                progressBar.Visibility = Visibility.Collapsed;
                return;
            }

            progressBar.Maximum = pcs.Count;
            dataGridResults.ItemsSource = null; // Очищаем таблицу перед новым сканированием

            var results = await Task.Run(() => ScanComputersParallel(pcs));

            dataGridResults.ItemsSource = results;

            btnScan.IsEnabled = true;
            btnScan.Content = "Сканировать";
            progressBar.Visibility = Visibility.Collapsed;
            MessageBox.Show($"Сканирование завершено! Обработано ПК: {results.Count}", "Готово");
        }

        private List<PcInfo> ScanComputersParallel(List<string> pcs)
        {
            var results = new ConcurrentBag<PcInfo>(); // Потокобезопасная коллекция
            int processedCount = 0;

            // Настройки подключения: тайм-аут 3 секунды, чтобы не ждать выключенные ПК
            var options = new ConnectionOptions
            {
                Timeout = TimeSpan.FromSeconds(3)
            };

            // Запускаем в 15 потоков одновременно
            Parallel.ForEach(pcs, new ParallelOptions { MaxDegreeOfParallelism = 15 }, (pc) =>
            {
                var pcName = pc.Trim();
                try
                {
                    var info = new PcInfo { ComputerName = pcName };
                    var scope = new ManagementScope($"\\\\{pcName}\\root\\cimv2", options);
                    scope.Connect(); // Проверяем связь

                    // 1. Модель, ОЗУ и ПОЛЬЗОВАТЕЛЬ
                    using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Model, TotalPhysicalMemory, UserName FROM Win32_ComputerSystem")))
                    {
                        foreach (ManagementObject queryObj in searcher.Get())
                        {
                            info.Model = queryObj["Model"]?.ToString();
                            string rawUser = queryObj["UserName"]?.ToString();
                            info.UserName = string.IsNullOrEmpty(rawUser) ? "Никто не вошел" : rawUser;
                            ulong ramBytes = Convert.ToUInt64(queryObj["TotalPhysicalMemory"] ?? 0);
                            info.RAM_GB = Math.Round((double)ramBytes / (1024 * 1024 * 1024), 1).ToString() + " GB";
                        }
                    }

                    // 2. Процессор
                    using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name FROM Win32_Processor")))
                    {
                        foreach (ManagementObject queryObj in searcher.Get())
                        {
                            info.CPU = queryObj["Name"]?.ToString()?.Trim().Replace("  ", " ");
                        }
                    }

                    // 3. IP-адрес
                    using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT IPAddress FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True")))
                    {
                        foreach (ManagementObject queryObj in searcher.Get())
                        {
                            string[] ips = (string[])queryObj["IPAddress"];
                            if (ips != null && ips.Length > 0)
                            {
                                info.IPAddress = ips.FirstOrDefault(ip => ip.Contains("."));
                                if (!string.IsNullOrEmpty(info.IPAddress)) break;
                            }
                        }
                    }

                    // 4. ОС
                    using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Caption FROM Win32_OperatingSystem")))
                    {
                        foreach (ManagementObject queryObj in searcher.Get())
                        {
                            string rawOs = queryObj["Caption"]?.ToString() ?? "";
                            info.OS = rawOs.Replace("Microsoft ", "").Trim();
                        }
                    }

                    // 5. Диски
                    var diskList = new List<string>();
                    using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Model, Size, MediaType FROM Win32_DiskDrive")))
                    {
                        foreach (ManagementObject queryObj in searcher.Get())
                        {
                            string model = queryObj["Model"]?.ToString() ?? "Unknown Disk";
                            ulong sizeBytes = Convert.ToUInt64(queryObj["Size"] ?? 0);
                            double sizeGb = Math.Round((double)sizeBytes / (1024 * 1024 * 1024), 0);
                            string mediaType = queryObj["MediaType"]?.ToString() ?? "";

                            string diskType = (mediaType.Contains("SSD") || model.ToUpper().Contains("SSD") || model.ToUpper().Contains("NVME")) ? "SSD" : "HDD";
                            diskList.Add($"{diskType} {sizeGb}GB ({model.Trim()})");
                        }
                    }
                    info.Disks = diskList.Count > 0 ? string.Join(" | ", diskList) : "Нет данных";

                    results.Add(info);
                }
                catch (Exception ex)
                {
                    // Временно выводим ошибку, чтобы понять причину
                    results.Add(new PcInfo
                    {
                        ComputerName = pcName,
                        Model = "ОШИБКА",
                        OS = ex.Message // Сюда попадёт текст ошибки WMI
                    });
                }
                finally
                {
                    // Обновляем прогресс-бар. 
                    // Используем Dispatcher, так как мы работаем в фоновом потоке, а UI принадлежит главному.
                    Interlocked.Increment(ref processedCount);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        progressBar.Value = processedCount;
                    });
                }
            });

            // Возвращаем только те ПК, которые ответили, отсортированные по имени
            return results.OrderBy(r => r.ComputerName).ToList();
        }
        private async void BtnGetDomainPCs_Click(object sender, RoutedEventArgs e)
        {
            btnGetDomainPCs.IsEnabled = false;
            btnGetDomainPCs.Content = "Загрузка...";

            try
            {
                // Запускаем в фоне, чтобы не зависал интерфейс
                var pcs = await Task.Run(() => GetDomainComputers());

                if (pcs.Count > 0)
                {
                    // Заполняем текстовое поле полученным списком
                    txtComputers.Text = string.Join(Environment.NewLine, pcs);
                    MessageBox.Show($"Успешно загружено ПК: {pcs.Count}", "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("В домене не найдено ни одного компьютера.", "Пусто", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка доступа к домену: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnGetDomainPCs.IsEnabled = true;
                btnGetDomainPCs.Content = "Загрузить ПК из домена";
            }
        }

        // Сам метод получения списка через LDAP
        private List<string> GetDomainComputers()
        {
            var computerNames = new List<string>();

            try
            {
                // Получаем корневой путь текущего домена
                using (var rootEntry = new DirectoryEntry("LDAP://RootDSE"))
                {
                    string defaultNamingContext = rootEntry.Properties["defaultNamingContext"].Value.ToString();

                    // Ищем компьютеры в домене
                    using (var searcher = new DirectorySearcher($"LDAP://{defaultNamingContext}"))
                    {
                        // Фильтр: ищем только объекты типа "computer"
                        searcher.Filter = "(objectClass=computer)";
                        searcher.PropertiesToLoad.Add("name"); // Нам нужно только имя
                        searcher.PageSize = 1000; // Важно для больших доменов

                        foreach (SearchResult result in searcher.FindAll())
                        {
                            if (result.Properties.Contains("name"))
                            {
                                computerNames.Add(result.Properties["name"][0].ToString());
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                throw; // Пробрасываем ошибку наверх, чтобы показать пользователю
            }

            return computerNames.OrderBy(x => x).ToList(); // Сортируем по алфавиту
        }
    }
}