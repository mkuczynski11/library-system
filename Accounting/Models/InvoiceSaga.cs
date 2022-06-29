﻿using Accounting.Configuration;
using Common;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Accounting.Models
{
    public class InvoiceSagaData : SagaStateMachineInstance
    {
        public Guid CorrelationId { get; set; }
        public Guid? TimeoutId { get; set; }
        public string CurrentState { get; set; }
    }

    public class InvoiceSaga : MassTransitStateMachine<InvoiceSagaData>, IDisposable
    {
        private readonly ILogger<InvoiceSaga> _logger;
        private readonly IServiceScope _scope;

        public State AwaitingPublishing { get; private set; }
        public State AwaitingPayment { get; private set; }

        public Event<AccountingInvoiceStart> AccountingInvoiceStartEvent { get; private set; }
        public Event<AccountingInvoicePublish> AccountingInvoicePublishEvent { get; private set; }
        public Event<AccountingInvoiceCancel> AccountingInvoiceCancelEvent { get; private set; }
        public Event<AccountingInvoicePaid> AccountingInvoicePaidEvent { get; private set; }

        public Schedule<InvoiceSagaData, AccountingInvoicePaymentTimeoutExpired> AccountingInvoicePaymentTimeout { get; private set; }


        public InvoiceSaga(IServiceProvider services, IConfiguration configuration, ILogger<InvoiceSaga> logger)
        {
            _logger = logger;
            _scope = services.CreateScope();

            var sagaConfiguration = configuration.GetSection("InvoiceSaga").Get<InvoiceSagaConfiguration>();

            InstanceState(x => x.CurrentState);

            Schedule(() => AccountingInvoicePaymentTimeout, instance => instance.TimeoutId, s =>
            {
                s.Delay = TimeSpan.FromSeconds(sagaConfiguration.PaymentTimeoutSeconds);
                s.Received = r => r.CorrelateById(context => context.Message.InvoiceId);
            });

            Initially(
                When(AccountingInvoiceStartEvent)
                .Then(context =>
                {
                    _logger.LogInformation($"Order ID={context.Message.CorrelationId}, preparing invoice.");

                    Invoice invoice = new Invoice(context.Message.CorrelationId.ToString(),
                        context.Message.BookID,
                        context.Message.BookName,
                        context.Message.BookQuantity,
                        context.Message.BookPrice,
                        context.Message.BookDiscount,
                        context.Message.DeliveryMethod,
                        context.Message.DeliveryPrice);

                    var invoices = _scope.ServiceProvider.GetRequiredService<InvoiceContext>();
                    invoices.InvoiceItems.Add(invoice);
                    invoices.SaveChanges();
                })
                .TransitionTo(AwaitingPublishing)
                );

            During(AwaitingPublishing,
                When(AccountingInvoicePublishEvent)
                .Then(context =>
                {
                    _logger.LogInformation($"Order ID={context.Message.CorrelationId}, publishing invoice.");

                    var invoices = _scope.ServiceProvider.GetRequiredService<InvoiceContext>();
                    Invoice invoice = invoices.InvoiceItems
                        .SingleOrDefault(o => o.ID.Equals(context.Message.CorrelationId.ToString()));
                    if (invoice != null)
                    {
                        invoice.IsPublic = true;
                        invoices.InvoiceItems.Update(invoice);
                        invoices.SaveChanges();
                    }
                })
                .Schedule(AccountingInvoicePaymentTimeout, context => context.Init<AccountingInvoicePaymentTimeoutExpired>(new
                {
                    InvoiceId = context.Message.CorrelationId
                }))
                .TransitionTo(AwaitingPayment),
                When(AccountingInvoiceCancelEvent)
                .Then(context =>
                {
                    logger.LogError($"Order ID={context.Message.CorrelationId}, publishing invoice canceled.");

                    var invoices = _scope.ServiceProvider.GetRequiredService<InvoiceContext>();
                    Invoice invoice = invoices.InvoiceItems
                        .SingleOrDefault(o => o.ID.Equals(context.Message.CorrelationId.ToString()));
                    if (invoice != null)
                    {
                        invoice.IsPublic = false;
                        invoices.InvoiceItems.Update(invoice);
                        invoices.SaveChanges();
                    }
                })
                .Finalize()
                );

            During(AwaitingPayment,
                When(AccountingInvoicePaidEvent)
                .Unschedule(AccountingInvoicePaymentTimeout)
                .Then(context =>
                {
                    _logger.LogInformation($"Order ID={context.Message.CorrelationId}, invoice paid.");
                })
                .Finalize(),

                When(AccountingInvoicePaymentTimeout.Received)
                .Then(context =>
                {
                    _logger.LogError($"Order ID={context.Message.InvoiceId}, payment time expired.");

                    var invoices = _scope.ServiceProvider.GetRequiredService<InvoiceContext>();
                    Invoice invoice = invoices.InvoiceItems
                        .SingleOrDefault(o => o.ID.Equals(context.Message.InvoiceId.ToString()));
                    if (invoice != null)
                    {
                        invoice.IsCanceled = true;
                        invoices.InvoiceItems.Update(invoice);
                        invoices.SaveChanges();
                    }
                })
                .PublishAsync(context => context.Init<AccountingInvoiceNotPaid>(new
                {
                    CorrelationId = context.Message.InvoiceId
                }))
                .Finalize()
                );

            SetCompletedWhenFinalized();
        }

        public void Dispose()
        {
            _scope?.Dispose();
        }
    }
}
