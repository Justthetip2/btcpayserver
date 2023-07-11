using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin.Protocol;
using PayoutData = BTCPayServer.Data.PayoutData;
using PayoutProcessorData = BTCPayServer.Data.PayoutProcessorData;

namespace BTCPayServer.PayoutProcessors;

public class AutomatedPayoutConstants
{
    public const double MinIntervalMinutes = 1.0;
    public const double MaxIntervalMinutes = 24 * 60; //1 day
    public static void ValidateInterval(ModelStateDictionary modelState, TimeSpan timeSpan, string parameterName)
    {
        if (timeSpan < TimeSpan.FromMinutes(AutomatedPayoutConstants.MinIntervalMinutes))
        {
            modelState.AddModelError(parameterName, $"The minimum interval is {MinIntervalMinutes * 60} seconds");
        }
        if (timeSpan > TimeSpan.FromMinutes(AutomatedPayoutConstants.MaxIntervalMinutes))
        {
            modelState.AddModelError(parameterName, $"The maximum interval is {MaxIntervalMinutes * 60} seconds");
        }
    }
}
public abstract class BaseAutomatedPayoutProcessor<T> : BaseAsyncService where T : AutomatedPayoutBlob
{
    protected readonly StoreRepository _storeRepository;
    protected readonly PayoutProcessorData PayoutProcessorSettings;
    protected readonly ApplicationDbContextFactory _applicationDbContextFactory;
    protected readonly BTCPayNetworkProvider _btcPayNetworkProvider;
    protected readonly PaymentMethodId PaymentMethodId;
    private readonly IPluginHookService _pluginHookService;
    private readonly EventAggregator _eventAggregator;

    protected BaseAutomatedPayoutProcessor(
        ILoggerFactory logger,
        StoreRepository storeRepository,
        PayoutProcessorData payoutProcessorSettings,
        ApplicationDbContextFactory applicationDbContextFactory,
        BTCPayNetworkProvider btcPayNetworkProvider,
        IPluginHookService pluginHookService,
        EventAggregator eventAggregator) : base(logger.CreateLogger($"{payoutProcessorSettings.Processor}:{payoutProcessorSettings.StoreId}:{payoutProcessorSettings.PaymentMethod}"))
    {
        _storeRepository = storeRepository;
        PayoutProcessorSettings = payoutProcessorSettings;
        PaymentMethodId = PayoutProcessorSettings.GetPaymentMethodId();
        _applicationDbContextFactory = applicationDbContextFactory;
        _btcPayNetworkProvider = btcPayNetworkProvider;
        _pluginHookService = pluginHookService;
        _eventAggregator = eventAggregator;
        this.NoLogsOnExit = true;
    }

    internal override Task[] InitializeTasks()
    {
        _subscription = _eventAggregator.SubscribeAsync<PayoutEvent>(OnPayoutEvent);
        return new[] { CreateLoopTask(Act) };
    }
    

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription.Dispose();
        return base.StopAsync(cancellationToken);
    }

    private Task OnPayoutEvent(PayoutEvent arg)
    {
        if (arg.Type == PayoutEvent.PayoutEventType.Approved && 
            PayoutProcessorSettings.StoreId == arg.Payout.StoreDataId &&
            arg.Payout.GetPaymentMethodId() == PaymentMethodId &&
            GetBlob(PayoutProcessorSettings).ProcessNewPayoutsInstantly)
        {
            SkipInterval();
        }
        return Task.CompletedTask;
    }

    protected abstract Task Process(ISupportedPaymentMethod paymentMethod, List<PayoutData> payouts);

    private async Task Act()
    {
        _timerCTs = null;
        var store = await _storeRepository.FindStore(PayoutProcessorSettings.StoreId);
        var paymentMethod = store?.GetEnabledPaymentMethods(_btcPayNetworkProvider)?.FirstOrDefault(
            method =>
                method.PaymentId == PaymentMethodId);

        var blob = GetBlob(PayoutProcessorSettings);
        if (paymentMethod is not null)
        {

            // Allow plugins to do something before the automatic payouts are executed
            await _pluginHookService.ApplyFilter("before-automated-payout-processing",
                new BeforePayoutFilterData(store, paymentMethod));

            await using var context = _applicationDbContextFactory.CreateContext();
            var payouts = await PullPaymentHostedService.GetPayouts(
                new PullPaymentHostedService.PayoutQuery()
                {
                    States = new[] { PayoutState.AwaitingPayment },
                    PaymentMethods = new[] { PayoutProcessorSettings.PaymentMethod },
                    Stores = new[] { PayoutProcessorSettings.StoreId }
                }, context, CancellationToken);
            if (payouts.Any())
            {
                Logs.PayServer.LogInformation($"{payouts.Count} found to process. Starting (and after will sleep for {blob.Interval})");
                await Process(paymentMethod, payouts);
                await context.SaveChangesAsync();

                // Allow plugins do to something after automatic payout processing
                await _pluginHookService.ApplyFilter("after-automated-payout-processing",
                    new AfterPayoutFilterData(store, paymentMethod, payouts));
            }
        }

        // Clip interval
        if (blob.Interval < TimeSpan.FromMinutes(AutomatedPayoutConstants.MinIntervalMinutes))
            blob.Interval = TimeSpan.FromMinutes(AutomatedPayoutConstants.MinIntervalMinutes);
        if (blob.Interval > TimeSpan.FromMinutes(AutomatedPayoutConstants.MaxIntervalMinutes))
            blob.Interval = TimeSpan.FromMinutes(AutomatedPayoutConstants.MaxIntervalMinutes);
        
        _timerCTs??= new CancellationTokenSource();
        try
        {
            await Task.Delay(blob.Interval, CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, _timerCTs.Token).Token);
        }
        catch (TaskCanceledException)
        {
        }
    }

    private CancellationTokenSource _timerCTs;
    private IEventAggregatorSubscription _subscription;

    public void SkipInterval()
    {
        _timerCTs ??= new CancellationTokenSource();
        _timerCTs?.Cancel();
    }
    

    public static T GetBlob(PayoutProcessorData payoutProcesserSettings)
    {
        return payoutProcesserSettings.HasTypedBlob<T>().GetBlob();
    }
}
