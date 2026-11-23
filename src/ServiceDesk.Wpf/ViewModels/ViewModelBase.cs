using Microsoft.Data.SqlClient;
using ServiceDesk.Data.Connection;
using ServiceDesk.Domain;
using ServiceDesk.Wpf.Mvvm;

namespace ServiceDesk.Wpf.ViewModels;

/// <summary>Common busy / error / status state + centralised exception-to-message translation.</summary>
public abstract class ViewModelBase : ObservableObject
{
    private bool _isBusy;
    private string? _errorMessage;
    private string? _statusMessage;

    public bool IsBusy { get => _isBusy; protected set => SetProperty(ref _isBusy, value); }
    public string? ErrorMessage { get => _errorMessage; protected set => SetProperty(ref _errorMessage, value); }
    public string? StatusMessage { get => _statusMessage; protected set => SetProperty(ref _statusMessage, value); }

    public abstract string Title { get; }

    /// <summary>Runs async work off the UI's critical path; the UI stays responsive (async/await all the way down).</summary>
    protected async Task<bool> RunAsync(Func<Task> work, string? successMessage = null)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await work();
            if (successMessage is not null) StatusMessage = successMessage;
            return true;
        }
        catch (ValidationException vex)
        {
            ErrorMessage = "Please fix the following:" + Environment.NewLine + "• " + string.Join(Environment.NewLine + "• ", vex.Errors);
        }
        catch (DomainException dex)
        {
            ErrorMessage = dex.Message;
        }
        catch (SqlException sex)
        {
            ErrorMessage = "Database error: " + ConnectionTester.Explain(sex);
        }
        catch (InvalidOperationException iex)
        {
            ErrorMessage = iex.Message;   // e.g. invalid connection settings
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Operation cancelled.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Unexpected error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        return false;
    }
}
