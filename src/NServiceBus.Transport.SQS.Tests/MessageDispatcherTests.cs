﻿namespace NServiceBus.Transport.SQS.Tests
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json.Nodes;
    using System.Threading.Tasks;
    using Amazon.SimpleNotificationService.Model;
    using Amazon.SQS.Model;
    using Configure;
    using DelayedDelivery;
    using NServiceBus;
    using NUnit.Framework;
    using Particular.Approvals;
    using Routing;
    using Settings;
    using SQS;
    using Transport;

    [TestFixture]
    public class MessageDispatcherTests
    {
        static readonly TimeSpan ExpectedTtbr = TimeSpan.MaxValue.Subtract(TimeSpan.FromHours(1));

        const string ExpectedReplyToAddress = "TestReplyToAddress";

        [Test]
        public async Task Does_not_send_extra_properties_in_payload_by_default()
        {
            var mockSqsClient = new MockSqsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, null, new QueueCache(mockSqsClient,
                dest => QueueCache.GetSqsQueueName(dest, "")), null, null, 15 * 60);

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage("1234", new Dictionary<string, string>
                    {
                        {TransportHeaders.TimeToBeReceived, ExpectedTtbr.ToString()},
                        {Headers.ReplyToAddress, ExpectedReplyToAddress},
                        {Headers.MessageId, "093C17C6-D32E-44FE-9134-65C10C1287EB"}
                    }, Encoding.Default.GetBytes("{}")),
                    new UnicastAddressTag("address"),
                    new DispatchProperties(),
                    DispatchConsistency.Isolated));

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.IsNotEmpty(mockSqsClient.RequestsSent, "No requests sent");
            var request = mockSqsClient.RequestsSent.First();

            Approver.Verify(request.MessageBody);
        }

        public static IEnumerable MessageIdConstraintsSource
        {
            get
            {
                yield return new TestCaseData(new DispatchProperties
                {
                    DelayDeliveryWith = new DelayDeliveryWith(TimeSpan.FromMinutes(30))
                });

                yield return new TestCaseData(new DispatchProperties());
            }
        }

        [Theory]
        [TestCaseSource(nameof(MessageIdConstraintsSource))]
        public async Task Includes_message_id_in_message_attributes(DispatchProperties properties)
        {
            var mockSqsClient = new MockSqsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, null, new QueueCache(mockSqsClient,
                dest => QueueCache.GetSqsQueueName(dest, "")), null, null, 15 * 60);

            var expectedId = "1234";

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(expectedId, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address"),
                    properties,
                    DispatchConsistency.Isolated));

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            var sentMessage = mockSqsClient.RequestsSent.First();
            Assert.AreEqual(expectedId, sentMessage.MessageAttributes[Headers.MessageId].StringValue);
        }

        [Test]
        public async Task Should_dispatch_isolated_unicast_operations()
        {
            var mockSqsClient = new MockSqsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, null, new QueueCache(mockSqsClient,
                dest => QueueCache.GetSqsQueueName(dest, "")), null, null, 15 * 60);

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address1"),
                    new DispatchProperties(),
                    DispatchConsistency.Isolated),
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address2"),
                    new DispatchProperties(),
                    DispatchConsistency.Isolated));

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.IsEmpty(mockSqsClient.BatchRequestsSent);
            Assert.AreEqual(2, mockSqsClient.RequestsSent.Count);
            Assert.That(mockSqsClient.RequestsSent.Select(t => t.QueueUrl), Is.EquivalentTo(new[]
            {
                "address1",
                "address2"
            }));
        }

        [Test]
        public async Task Should_dispatch_isolated_multicast_operations()
        {
            var mockSnsClient = new MockSnsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), null, mockSnsClient, null,
                new TopicCache(mockSnsClient, new SettingsHolder(), new EventToTopicsMappings(),
                    new EventToEventsMappings(), (type, s) => TopicNameHelper.GetSnsTopicName(type, ""), ""), null,
                15 * 60);

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new MulticastAddressTag(typeof(Event)),
                    new DispatchProperties(),
                    DispatchConsistency.Isolated),
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new MulticastAddressTag(typeof(AnotherEvent)),
                    new DispatchProperties(),
                    DispatchConsistency.Isolated));

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.IsEmpty(mockSnsClient.BatchRequestsPublished);
            Assert.AreEqual(2, mockSnsClient.PublishedEvents.Count);
            Assert.That(mockSnsClient.PublishedEvents.Select(t => t.TopicArn), Is.EquivalentTo(new[]
            {
                "arn:aws:sns:us-west-2:123456789012:NServiceBus-Transport-SQS-Tests-MessageDispatcherTests-Event",
                "arn:aws:sns:us-west-2:123456789012:NServiceBus-Transport-SQS-Tests-MessageDispatcherTests-AnotherEvent"
            }));
        }

        [Test]
        public async Task Should_deduplicate_if_compatibility_mode_is_enabled_and_subscription_found()
        {
            var mockSnsClient = new MockSnsClient();
            var mockSqsClient = new MockSqsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, mockSnsClient, new QueueCache(null,
                    dest => QueueCache.GetSqsQueueName(dest, "")),
                new TopicCache(mockSnsClient, new SettingsHolder(), new EventToTopicsMappings(), new EventToEventsMappings(), (type, s) => TopicNameHelper.GetSnsTopicName(type, ""), ""),
                null, 15 * 60);

            mockSnsClient.ListSubscriptionsByTopicResponse = topic => new ListSubscriptionsByTopicResponse
            {
                Subscriptions = new List<Subscription>
                {
                    new Subscription { Endpoint = "arn:abc", SubscriptionArn = "arn:subscription" }
                }
            };

            var messageId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, string>()
            {
                {Headers.EnclosedMessageTypes, typeof(Event).AssemblyQualifiedName},
                {Headers.MessageIntent, MessageIntent.Publish.ToString() }
            };
            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(messageId, headers, Encoding.UTF8.GetBytes("{}")),
                    new MulticastAddressTag(typeof(Event))),
                new TransportOperation(
                    new OutgoingMessage(messageId, headers, Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("abc"))
            );

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.AreEqual(1, mockSnsClient.BatchRequestsPublished.Count);
            Assert.IsEmpty(mockSqsClient.RequestsSent);
            Assert.IsEmpty(mockSqsClient.BatchRequestsSent);
        }

        [Test]
        public async Task Should_not_deduplicate_if_compatibility_mode_is_enabled_and_no_subscription_found()
        {
            var mockSnsClient = new MockSnsClient();
            var mockSqsClient = new MockSqsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, mockSnsClient, new QueueCache(mockSqsClient,
                    dest => QueueCache.GetSqsQueueName(dest, "")),
                new TopicCache(mockSnsClient, new SettingsHolder(), new EventToTopicsMappings(), new EventToEventsMappings(), (type, s) => TopicNameHelper.GetSnsTopicName(type, ""), ""),
                null, 15 * 60);

            var messageId = Guid.NewGuid().ToString();
            var headers = new Dictionary<string, string>()
            {
                {Headers.EnclosedMessageTypes, typeof(Event).AssemblyQualifiedName}
            };
            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(messageId, headers, Encoding.UTF8.GetBytes("{}")),
                    new MulticastAddressTag(typeof(Event))),
                new TransportOperation(
                    new OutgoingMessage(messageId, headers, Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("abc"))
            );

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.AreEqual(1, mockSnsClient.BatchRequestsPublished.Count);
            Assert.AreEqual(1, mockSqsClient.BatchRequestsSent.Count);
        }

        [Test]
        public async Task Should_not_dispatch_multicast_operation_if_topic_doesnt_exist()
        {
            var mockSnsClient = new MockSnsClient
            {
                FindTopicAsyncResponse = topic => null
            };

            var dispatcher = new MessageDispatcher(new SettingsHolder(), null, mockSnsClient, new QueueCache(null,
                    dest => QueueCache.GetSqsQueueName(dest, "")),
                new TopicCache(mockSnsClient, new SettingsHolder(), new EventToTopicsMappings(), new EventToEventsMappings(), (type, s) => TopicNameHelper.GetSnsTopicName(type, ""), ""),
                null, 15 * 60);

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new MulticastAddressTag(typeof(Event)))
            );

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.IsEmpty(mockSnsClient.PublishedEvents);
            Assert.IsEmpty(mockSnsClient.CreateTopicRequests);
        }

        [Test]
        public async Task Should_not_dispatch_multicast_operation_if_event_type_is_object()
        {
            var mockSnsClient = new MockSnsClient
            {
                //given that a subscriber never sets up a topic for object this has to return null
                FindTopicAsyncResponse = topic => null
            };

            var dispatcher = new MessageDispatcher(new SettingsHolder(), null, mockSnsClient, new QueueCache(null,
                    dest => QueueCache.GetSqsQueueName(dest, "")),
                new TopicCache(mockSnsClient, new SettingsHolder(), new EventToTopicsMappings(), new EventToEventsMappings(), (type, s) => TopicNameHelper.GetSnsTopicName(type, ""), ""),
                null, 15 * 60);

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new MulticastAddressTag(typeof(object)))
            );

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.IsEmpty(mockSnsClient.PublishedEvents);
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public async Task Should_upload_large_multicast_operations_request_to_s3(bool wrapMessage)
        {
            string keyPrefix = "somePrefix";

            var mockS3Client = new MockS3Client();
            var mockSnsClient = new MockSnsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), null, mockSnsClient, new QueueCache(null,
                    dest => QueueCache.GetSqsQueueName(dest, "")),
                new TopicCache(mockSnsClient, new SettingsHolder(), new EventToTopicsMappings(), new EventToEventsMappings(), (type, s) => TopicNameHelper.GetSnsTopicName(type, ""), ""),
                new S3Settings("someBucket", keyPrefix, mockS3Client), 15 * 60, wrapOutgoingMessages: wrapMessage);

            var longBodyMessageId = Guid.NewGuid().ToString();
            /* Crazy long message id will cause the message to go over limits because attributes count as well */
            var crazyLongMessageId = new string('x', 256 * 1024);

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(longBodyMessageId, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(new string('x', 256 * 1024))),
                    new MulticastAddressTag(typeof(Event)),
                    new DispatchProperties(),
                    DispatchConsistency.Isolated),
                new TransportOperation( /* Crazy long message id will cause the message to go over limits because attributes count as well */
                    new OutgoingMessage(crazyLongMessageId, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new MulticastAddressTag(typeof(AnotherEvent)),
                    new DispatchProperties(),
                    DispatchConsistency.Isolated));

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.AreEqual(2, mockSnsClient.PublishedEvents.Count);
            Assert.AreEqual(2, mockS3Client.PutObjectRequestsSent.Count);

            var longBodyMessageUpload = mockS3Client.PutObjectRequestsSent.Single(por => por.Key == $"{keyPrefix}/{longBodyMessageId}");
            var crazyLongMessageUpload = mockS3Client.PutObjectRequestsSent.Single(por => por.Key == $"{keyPrefix}/{crazyLongMessageId}");

            Assert.AreEqual("someBucket", longBodyMessageUpload.BucketName);
            Assert.AreEqual("someBucket", crazyLongMessageUpload.BucketName);
            if (wrapMessage)
            {
                StringAssert.Contains($@"""Body"":"""",""S3BodyKey"":""{longBodyMessageUpload.Key}", mockSnsClient.PublishedEvents.Single(pr => pr.MessageAttributes[Headers.MessageId].StringValue == longBodyMessageId).Message);
                StringAssert.Contains($@"""Body"":"""",""S3BodyKey"":""{crazyLongMessageUpload.Key}", mockSnsClient.PublishedEvents.Single(pr => pr.MessageAttributes[Headers.MessageId].StringValue == crazyLongMessageId).Message);
            }
            else
            {
                Assert.AreEqual(longBodyMessageUpload.Key, mockSnsClient.PublishedEvents.Single(pr => pr.MessageAttributes[Headers.MessageId].StringValue == longBodyMessageId).MessageAttributes[TransportHeaders.S3BodyKey].StringValue);
                Assert.AreEqual(crazyLongMessageUpload.Key, mockSnsClient.PublishedEvents.Single(pr => pr.MessageAttributes[Headers.MessageId].StringValue == crazyLongMessageId).MessageAttributes[TransportHeaders.S3BodyKey].StringValue);
            }
        }

        [Test]
        public void Should_raise_queue_does_not_exists_for_delayed_delivery_for_isolated_dispatch()
        {
            var mockSqsClient = new MockSqsClient
            {
                RequestResponse = req => throw new QueueDoesNotExistException("Queue does not exist")
            };

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, null, new QueueCache(mockSqsClient,
                    dest => QueueCache.GetSqsQueueName(dest, "")), null, null, 15 * 60);

            var properties = new DispatchProperties
            {
                DelayDeliveryWith = new DelayDeliveryWith(TimeSpan.FromMinutes(30))
            };

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address1"),
                    properties,
                    DispatchConsistency.Isolated));

            var transportTransaction = new TransportTransaction();

            var exception = Assert.ThrowsAsync<QueueDoesNotExistException>(async () => await dispatcher.Dispatch(transportOperations, transportTransaction));
            StringAssert.StartsWith("Destination 'address1' doesn't support delayed messages longer than", exception.Message);
        }

        [Test]
        public async Task Should_batch_non_isolated_unicast_operations()
        {
            var mockSqsClient = new MockSqsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, null, new QueueCache(mockSqsClient,
                dest => QueueCache.GetSqsQueueName(dest, "")), null, null, 15 * 60);

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address1"),
                    new DispatchProperties(),
                    DispatchConsistency.Default),
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address2"),
                    new DispatchProperties(),
                    DispatchConsistency.Default));

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.IsEmpty(mockSqsClient.RequestsSent);
            Assert.AreEqual(2, mockSqsClient.BatchRequestsSent.Count);
            Assert.AreEqual("address1", mockSqsClient.BatchRequestsSent.ElementAt(0).QueueUrl);
            Assert.AreEqual("address2", mockSqsClient.BatchRequestsSent.ElementAt(1).QueueUrl);
        }

        [Test]
        public async Task Should_batch_non_isolated_multicast_operations()
        {
            var mockSnsClient = new MockSnsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), null, mockSnsClient, new QueueCache(null,
                dest => QueueCache.GetSqsQueueName(dest, "")),
                new TopicCache(mockSnsClient, new SettingsHolder(), new EventToTopicsMappings(),
                    new EventToEventsMappings(), (type, s) => TopicNameHelper.GetSnsTopicName(type, ""), ""), null,
                15 * 60);

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new MulticastAddressTag(typeof(Event)),
                    new DispatchProperties(),
                    DispatchConsistency.Default),
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new MulticastAddressTag(typeof(AnotherEvent)),
                    new DispatchProperties(),
                    DispatchConsistency.Default));

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.IsEmpty(mockSnsClient.PublishedEvents);
            Assert.AreEqual(2, mockSnsClient.BatchRequestsPublished.Count);
            Assert.AreEqual("arn:aws:sns:us-west-2:123456789012:NServiceBus-Transport-SQS-Tests-MessageDispatcherTests-Event", mockSnsClient.BatchRequestsPublished.ElementAt(0).TopicArn);
            Assert.AreEqual("arn:aws:sns:us-west-2:123456789012:NServiceBus-Transport-SQS-Tests-MessageDispatcherTests-AnotherEvent", mockSnsClient.BatchRequestsPublished.ElementAt(1).TopicArn);
        }

        [Test]
        public void Should_raise_queue_does_not_exists_for_delayed_delivery_for_non_isolated_dispatch()
        {
            var mockSqsClient = new MockSqsClient
            {
                BatchRequestResponse = req => throw new QueueDoesNotExistException("Queue does not exist")
            };

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, null, new QueueCache(mockSqsClient,
                dest => QueueCache.GetSqsQueueName(dest, "")), null, null, 15 * 60);

            var properties = new DispatchProperties
            {
                DelayDeliveryWith = new DelayDeliveryWith(TimeSpan.FromMinutes(30))
            };

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address1"),
                    properties,
                    DispatchConsistency.Default),
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address1"),
                    properties,
                    DispatchConsistency.Default));

            var transportTransaction = new TransportTransaction();

            var exception = Assert.ThrowsAsync<QueueDoesNotExistException>(async () => await dispatcher.Dispatch(transportOperations, transportTransaction));
            StringAssert.StartsWith("Unable to send batch '1/1'. Destination 'address1' doesn't support delayed messages longer than", exception.Message);
        }

        [Test]
        public async Task Should_dispatch_non_batched_all_unicast_operations_that_failed_in_batch()
        {
            var mockSqsClient = new MockSqsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, null, new QueueCache(mockSqsClient,
                dest => QueueCache.GetSqsQueueName(dest, "")), null, null, 15 * 60);

            var firstMessageIdThatWillFail = Guid.NewGuid().ToString();
            var secondMessageIdThatWillFail = Guid.NewGuid().ToString();

            mockSqsClient.BatchRequestResponse = req =>
            {
                var firstMessageMatch = req.Entries.SingleOrDefault(x => x.MessageAttributes[Headers.MessageId].StringValue == firstMessageIdThatWillFail);
                if (firstMessageMatch != null)
                {
                    return new SendMessageBatchResponse
                    {
                        Failed = new List<Amazon.SQS.Model.BatchResultErrorEntry>
                        {
                            new()
                            {
                                Id = firstMessageMatch.Id,
                                Message = "You know why"
                            }
                        }
                    };
                }

                var secondMessageMatch = req.Entries.SingleOrDefault(x => x.MessageAttributes[Headers.MessageId].StringValue == secondMessageIdThatWillFail);
                if (secondMessageMatch != null)
                {
                    return new SendMessageBatchResponse
                    {
                        Failed = new List<Amazon.SQS.Model.BatchResultErrorEntry>
                        {
                            new()
                            {
                                Id = secondMessageMatch.Id,
                                Message = "You know why"
                            }
                        }
                    };
                }

                return new SendMessageBatchResponse();
            };

            var firstMessageThatWillBeSuccessful = Guid.NewGuid().ToString();
            var secondMessageThatWillBeSuccessful = Guid.NewGuid().ToString();

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(firstMessageIdThatWillFail, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address1"),
                    new DispatchProperties(),
                    DispatchConsistency.Default),
                new TransportOperation(
                    new OutgoingMessage(firstMessageThatWillBeSuccessful, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address1"),
                    new DispatchProperties(),
                    DispatchConsistency.Default),
                new TransportOperation(
                    new OutgoingMessage(secondMessageThatWillBeSuccessful, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address2"),
                    new DispatchProperties(),
                    DispatchConsistency.Default),
                new TransportOperation(
                    new OutgoingMessage(secondMessageIdThatWillFail, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address2"),
                    new DispatchProperties(),
                    DispatchConsistency.Default));

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.AreEqual(2, mockSqsClient.RequestsSent.Count);
            Assert.AreEqual(firstMessageIdThatWillFail, mockSqsClient.RequestsSent.ElementAt(0).MessageAttributes[Headers.MessageId].StringValue);
            Assert.AreEqual(secondMessageIdThatWillFail, mockSqsClient.RequestsSent.ElementAt(1).MessageAttributes[Headers.MessageId].StringValue);

            Assert.AreEqual(2, mockSqsClient.BatchRequestsSent.Count);
            CollectionAssert.AreEquivalent(new[] { firstMessageIdThatWillFail, firstMessageThatWillBeSuccessful }, mockSqsClient.BatchRequestsSent.ElementAt(0).Entries.Select(x => x.MessageAttributes[Headers.MessageId].StringValue));
            CollectionAssert.AreEquivalent(new[] { secondMessageIdThatWillFail, secondMessageThatWillBeSuccessful }, mockSqsClient.BatchRequestsSent.ElementAt(1).Entries.Select(x => x.MessageAttributes[Headers.MessageId].StringValue));
        }

        [Test]
        public async Task Should_dispatch_non_batched_all_multicast_operations_that_failed_in_batch()
        {
            var mockSnsClient = new MockSnsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), null, mockSnsClient, new QueueCache(null,
                dest => QueueCache.GetSqsQueueName(dest, "")), new TopicCache(mockSnsClient, new SettingsHolder(), new EventToTopicsMappings(), new EventToEventsMappings(), (type, s) => TopicNameHelper.GetSnsTopicName(type, ""), ""), null, 15 * 60);

            var firstMessageIdThatWillFail = Guid.NewGuid().ToString();
            var secondMessageIdThatWillFail = Guid.NewGuid().ToString();

            mockSnsClient.BatchRequestResponse = req =>
            {
                var firstMessageMatch = req.PublishBatchRequestEntries.SingleOrDefault(x => x.MessageAttributes[Headers.MessageId].StringValue == firstMessageIdThatWillFail);
                if (firstMessageMatch != null)
                {
                    return new PublishBatchResponse
                    {
                        Failed = new List<Amazon.SimpleNotificationService.Model.BatchResultErrorEntry>
                        {
                            new()
                            {
                                Id = firstMessageMatch.Id,
                                Message = "You know why"
                            }
                        }
                    };
                }

                var secondMessageMatch = req.PublishBatchRequestEntries.SingleOrDefault(x => x.MessageAttributes[Headers.MessageId].StringValue == secondMessageIdThatWillFail);
                if (secondMessageMatch != null)
                {
                    return new PublishBatchResponse
                    {
                        Failed = new List<Amazon.SimpleNotificationService.Model.BatchResultErrorEntry>
                        {
                            new()
                            {
                                Id = secondMessageMatch.Id,
                                Message = "You know why"
                            }
                        }
                    };
                }

                return new PublishBatchResponse();
            };

            var firstMessageThatWillBeSuccessful = Guid.NewGuid().ToString();
            var secondMessageThatWillBeSuccessful = Guid.NewGuid().ToString();

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(firstMessageIdThatWillFail, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new MulticastAddressTag(typeof(Event)),
                    new DispatchProperties(),
                    DispatchConsistency.Default),
                new TransportOperation(
                    new OutgoingMessage(firstMessageThatWillBeSuccessful, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new MulticastAddressTag(typeof(Event)),
                    new DispatchProperties(),
                    DispatchConsistency.Default),
                new TransportOperation(
                    new OutgoingMessage(secondMessageThatWillBeSuccessful, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new MulticastAddressTag(typeof(AnotherEvent)),
                    new DispatchProperties(),
                    DispatchConsistency.Default),
                new TransportOperation(
                    new OutgoingMessage(secondMessageIdThatWillFail, new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new MulticastAddressTag(typeof(AnotherEvent)),
                    new DispatchProperties(),
                    DispatchConsistency.Default));

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.AreEqual(2, mockSnsClient.PublishedEvents.Count);
            Assert.AreEqual(firstMessageIdThatWillFail, mockSnsClient.PublishedEvents.ElementAt(0).MessageAttributes[Headers.MessageId].StringValue);
            Assert.AreEqual(secondMessageIdThatWillFail, mockSnsClient.PublishedEvents.ElementAt(1).MessageAttributes[Headers.MessageId].StringValue);

            Assert.AreEqual(2, mockSnsClient.BatchRequestsPublished.Count);
            CollectionAssert.AreEquivalent(new[] { firstMessageIdThatWillFail, firstMessageThatWillBeSuccessful }, mockSnsClient.BatchRequestsPublished.ElementAt(0).PublishBatchRequestEntries.Select(x => x.MessageAttributes[Headers.MessageId].StringValue));
            CollectionAssert.AreEquivalent(new[] { secondMessageIdThatWillFail, secondMessageThatWillBeSuccessful }, mockSnsClient.BatchRequestsPublished.ElementAt(1).PublishBatchRequestEntries.Select(x => x.MessageAttributes[Headers.MessageId].StringValue));
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public async Task Should_upload_large_non_isolated_operations_to_s3(bool wrapMessage)
        {
            var mockS3Client = new MockS3Client();
            var mockSqsClient = new MockSqsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, null, new QueueCache(mockSqsClient,
                dest => QueueCache.GetSqsQueueName(dest, "")), null,
                new S3Settings("someBucket", "somePrefix", mockS3Client), 15 * 60, wrapOutgoingMessages: wrapMessage);

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes(new string('x', 256 * 1024))),
                    new UnicastAddressTag("address1"),
                    new DispatchProperties(),
                    DispatchConsistency.Default),
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes(new string('x', 256 * 1024))),
                    new UnicastAddressTag("address2"),
                    new DispatchProperties(),
                    DispatchConsistency.Default),
                new TransportOperation( /* Crazy long message id will cause the message to go over limits because attributes count as well */
                    new OutgoingMessage(new string('x', 256 * 1024), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address2"),
                    new DispatchProperties(),
                    DispatchConsistency.Default));

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.AreEqual(3, mockSqsClient.BatchRequestsSent.Count);
            Assert.AreEqual(3, mockS3Client.PutObjectRequestsSent.Count);

            var firstUpload = mockS3Client.PutObjectRequestsSent.ElementAt(0);
            var secondUpload = mockS3Client.PutObjectRequestsSent.ElementAt(1);
            var thirdUpload = mockS3Client.PutObjectRequestsSent.ElementAt(2);

            Assert.AreEqual("someBucket", firstUpload.BucketName);
            Assert.AreEqual("someBucket", secondUpload.BucketName);
            Assert.AreEqual("someBucket", thirdUpload.BucketName);
            if (wrapMessage)
            {
                StringAssert.Contains($@"""Body"":"""",""S3BodyKey"":""{firstUpload.Key}", mockSqsClient.BatchRequestsSent.ElementAt(0).Entries.ElementAt(0).MessageBody);
                StringAssert.Contains($@"""Body"":"""",""S3BodyKey"":""{secondUpload.Key}", mockSqsClient.BatchRequestsSent.ElementAt(1).Entries.ElementAt(0).MessageBody);
                StringAssert.Contains($@"""Body"":"""",""S3BodyKey"":""{thirdUpload.Key}", mockSqsClient.BatchRequestsSent.ElementAt(2).Entries.ElementAt(0).MessageBody);
            }
            else
            {
                Assert.AreEqual(firstUpload.Key, mockSqsClient.BatchRequestsSent.ElementAt(0).Entries.ElementAt(0).MessageAttributes[TransportHeaders.S3BodyKey].StringValue);
                Assert.AreEqual(secondUpload.Key, mockSqsClient.BatchRequestsSent.ElementAt(1).Entries.ElementAt(0).MessageAttributes[TransportHeaders.S3BodyKey].StringValue);
                Assert.AreEqual(thirdUpload.Key, mockSqsClient.BatchRequestsSent.ElementAt(2).Entries.ElementAt(0).MessageAttributes[TransportHeaders.S3BodyKey].StringValue);
            }
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public async Task Should_upload_large_isolated_operations_request_to_s3(bool wrapMessage)
        {
            var mockS3Client = new MockS3Client();
            var mockSqsClient = new MockSqsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, null, new QueueCache(mockSqsClient,
                dest => QueueCache.GetSqsQueueName(dest, "")), null,
                new S3Settings("someBucket", "somePrefix", mockS3Client), 15 * 60, wrapOutgoingMessages: wrapMessage);

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes(new string('x', 256 * 1024))),
                    new UnicastAddressTag("address1"),
                    new DispatchProperties(),
                    DispatchConsistency.Isolated),
                new TransportOperation(
                    new OutgoingMessage(Guid.NewGuid().ToString(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes(new string('x', 256 * 1024))),
                    new UnicastAddressTag("address2"),
                    new DispatchProperties(),
                    DispatchConsistency.Isolated),
                new TransportOperation( /* Crazy long message id will cause the message to go over limits because attributes count as well */
                    new OutgoingMessage(new string('x', 256 * 1024), new Dictionary<string, string>(), Encoding.UTF8.GetBytes("{}")),
                    new UnicastAddressTag("address2"),
                    new DispatchProperties(),
                    DispatchConsistency.Isolated));

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.AreEqual(3, mockSqsClient.RequestsSent.Count);
            Assert.AreEqual(3, mockS3Client.PutObjectRequestsSent.Count);

            var firstUpload = mockS3Client.PutObjectRequestsSent.ElementAt(0);
            var secondUpload = mockS3Client.PutObjectRequestsSent.ElementAt(1);
            var thirdUpload = mockS3Client.PutObjectRequestsSent.ElementAt(2);

            Assert.AreEqual("someBucket", firstUpload.BucketName);
            Assert.AreEqual("someBucket", secondUpload.BucketName);
            Assert.AreEqual("someBucket", thirdUpload.BucketName);
            if (wrapMessage)
            {
                StringAssert.Contains($@"""Body"":"""",""S3BodyKey"":""{firstUpload.Key}", mockSqsClient.RequestsSent.ElementAt(0).MessageBody);
                StringAssert.Contains($@"""Body"":"""",""S3BodyKey"":""{secondUpload.Key}", mockSqsClient.RequestsSent.ElementAt(1).MessageBody);
                StringAssert.Contains($@"""Body"":"""",""S3BodyKey"":""{thirdUpload.Key}", mockSqsClient.RequestsSent.ElementAt(2).MessageBody);
            }
            else
            {
                Assert.AreEqual(firstUpload.Key, mockSqsClient.RequestsSent.ElementAt(0).MessageAttributes[TransportHeaders.S3BodyKey].StringValue);
                Assert.AreEqual(secondUpload.Key, mockSqsClient.RequestsSent.ElementAt(1).MessageAttributes[TransportHeaders.S3BodyKey].StringValue);
                Assert.AreEqual(thirdUpload.Key, mockSqsClient.RequestsSent.ElementAt(2).MessageAttributes[TransportHeaders.S3BodyKey].StringValue);
            }
        }

        [Test]
        public async Task Should_base64_encode_by_default()
        {
            var mockSqsClient = new MockSqsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, null, new QueueCache(mockSqsClient,
                dest => QueueCache.GetSqsQueueName(dest, "")), null, null, 15 * 60);

            var msgBody = "my message body";
            var msgBodyByte = Encoding.UTF8.GetBytes(msgBody);

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage("1234", new Dictionary<string, string>
                    {
                        {TransportHeaders.TimeToBeReceived, ExpectedTtbr.ToString()},
                        {Headers.ReplyToAddress, ExpectedReplyToAddress}
                    }, msgBodyByte),
                    new UnicastAddressTag("address"),
                    new DispatchProperties(),
                    DispatchConsistency.Isolated));

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.IsNotEmpty(mockSqsClient.RequestsSent, "No requests sent");
            var request = mockSqsClient.RequestsSent.First();

            var bodyJson = JsonNode.Parse(request.MessageBody);

            Assert.AreEqual(Convert.ToBase64String(msgBodyByte), bodyJson["Body"].GetValue<string>());
        }

        [Test]
        public async Task Should_not_wrap_if_configured_not_to()
        {
            var mockSqsClient = new MockSqsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, null, new QueueCache(mockSqsClient,
                dest => QueueCache.GetSqsQueueName(dest, "")), null, null, 15 * 60, wrapOutgoingMessages: false);

            var msgBody = "my message body";
            var msgBodyByte = Encoding.UTF8.GetBytes(msgBody);

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage("1234", new Dictionary<string, string>
                    {
                        {TransportHeaders.TimeToBeReceived, ExpectedTtbr.ToString()},
                        {Headers.ReplyToAddress, ExpectedReplyToAddress},
                        {Headers.MessageId, "74d4f8e4-0fc7-4f09-8d46-0b76994e76d6"}
                    }, msgBodyByte),
                    new UnicastAddressTag("address"),
                    new DispatchProperties(),
                    DispatchConsistency.Isolated));

            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.IsNotEmpty(mockSqsClient.RequestsSent, "No requests sent");
            var request = mockSqsClient.RequestsSent.First();

            Approver.Verify(request);
        }

        [Test]
        [TestCase(true)]
        [TestCase(false)]
        public async Task Should_handle_empty_body_gracefully(bool wrapMessage)
        {
            var mockSqsClient = new MockSqsClient();

            var dispatcher = new MessageDispatcher(new SettingsHolder(), mockSqsClient, null, new QueueCache(mockSqsClient,
                dest => QueueCache.GetSqsQueueName(dest, "")), null, null, 15 * 60, wrapOutgoingMessages: wrapMessage);

            var messageid = Guid.NewGuid().ToString();

            var transportOperations = new TransportOperations(
                new TransportOperation(
                    new OutgoingMessage(
                        messageid,
                        new Dictionary<string, string>
                        {
                            [Headers.MessageId] = messageid
                        },
                        Array.Empty<byte>()
                        ),
                    new UnicastAddressTag("Somewhere"))
                );
            var transportTransaction = new TransportTransaction();

            await dispatcher.Dispatch(transportOperations, transportTransaction);

            Assert.IsNotEmpty(mockSqsClient.BatchRequestsSent, "No requests sent");
            var batch = mockSqsClient.BatchRequestsSent.First();
            var request = batch.Entries.First();

            Assert.That(request.MessageBody, Is.Not.Null.Or.Empty);
        }

        interface IEvent { }

        interface IMyEvent : IEvent { }
        class Event : IMyEvent { }
        class AnotherEvent : IMyEvent { }
    }
}