using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using ScreenAssistant.Configuration;
using ScreenAssistant.Core.Licensing;

namespace ScreenAssistant.Licensing;

public partial class ActivationWindow : Window
{
    private readonly ActivationClient _client;
    private bool _activating;

    internal ActivationWindow(ActivationClient client)
    {
        _client = client;
        InitializeComponent();

        try
        {
            if (LicenseReceipt.Load() is { } receipt)
            {
                ServerAddress.Text = receipt.Server;
                HardwareKey.IsChecked = receipt.HardwareBacked;
                ActivationStatus.Text = $"Сохранена лицензия {receipt.LicenseId:N}.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LocalFiles.Log("license-read", ex);
            ActivationStatus.Text = "Не удалось прочитать сохранённую лицензию.";
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_activating)
            e.Cancel = true;
    }

    private async void Balance_Click(object sender, RoutedEventArgs e)
    {
        if (_activating)
            return;

        _activating = true;
        BalanceButton.IsEnabled = ActivateButton.IsEnabled = false;
        try
        {
            if (LicenseReceipt.Load() is not { } receipt)
            {
                ActivationStatus.Text = "Сначала активируйте лицензию.";
                return;
            }

            ActivationStatus.Text = "Проверяем баланс…";
            using var key = WindowsDeviceKey.OpenOrCreate(receipt.HardwareBacked);
            var balance = await _client.ReadAvailableCreditsAsync(new Uri(receipt.Server),
                receipt.LicenseId, key, CancellationToken.None);
            ActivationStatus.Text = balance is { } credits
                ? $"Доступно кредитов: {credits}."
                : "Сервер не подтвердил лицензию.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or CryptographicException or HttpRequestException or OperationCanceledException)
        {
            LocalFiles.Log("license-balance", ex);
            ActivationStatus.Text = "Не удалось проверить баланс.";
        }
        finally
        {
            _activating = false;
            BalanceButton.IsEnabled = ActivateButton.IsEnabled = true;
        }
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_activating)
            return;

        if (!ActivationClient.TryParseServer(ServerAddress.Text, out var server) || server is null)
        {
            ActivationStatus.Text = "Введите HTTPS-адрес сервера.";
            return;
        }

        var code = ActivationCode.Password.Trim();
        if (code.Length != 64 || !code.All(Uri.IsHexDigit))
        {
            ActivationStatus.Text = "Проверьте код активации.";
            return;
        }

        var hardwareBacked = HardwareKey.IsChecked == true;
        _activating = true;
        BalanceButton.IsEnabled = ActivateButton.IsEnabled = ServerAddress.IsEnabled = ActivationCode.IsEnabled = HardwareKey.IsEnabled = false;
        ActivationStatus.Text = "Активируем лицензию…";
        try
        {
            using var key = WindowsDeviceKey.OpenOrCreate(hardwareBacked);
            var licenseId = await _client.ActivateAsync(server, code, key, CancellationToken.None);
            ActivationCode.Clear();
            ActivationStatus.Text = $"Активирована лицензия {licenseId:N}.";
            try
            {
                new LicenseReceipt(server.AbsoluteUri, licenseId, hardwareBacked).Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LocalFiles.Log("license-save", ex);
                ActivationStatus.Text += " Записать её на диск не удалось. Сообщите этот идентификатор оператору.";
            }
        }
        catch (CryptographicException ex)
        {
            LocalFiles.Log("license-key", ex);
            ActivationStatus.Text = hardwareBacked
                ? "Не удалось открыть ключ TPM. Можно снять галочку и активировать программный ключ."
                : "Не удалось открыть ключ устройства.";
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
        {
            ActivationStatus.Text = "Сервер отклонил активацию. Проверьте код или обратитесь к оператору.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            LocalFiles.Log("license-activate", ex);
            ActivationStatus.Text = "Не удалось подтвердить активацию. Проверьте подключение к серверу.";
        }
        finally
        {
            _activating = false;
            BalanceButton.IsEnabled = ActivateButton.IsEnabled = ServerAddress.IsEnabled = ActivationCode.IsEnabled = HardwareKey.IsEnabled = true;
        }
    }

}
