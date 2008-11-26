using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using log4net;
using Modbus.Message;

namespace Modbus.IO
{
	/// <summary>
	/// Modbus transport.
	/// Abstraction - http://en.wikipedia.org/wiki/Bridge_Pattern
	/// </summary>
	public abstract class ModbusTransport
	{
		private static readonly ILog _logger = LogManager.GetLogger(typeof(ModbusTransport));
		private object _syncLock = new object();
		private int _retries = Modbus.DefaultRetries;
		private int _waitToRetryMilliseconds = Modbus.DefaultWaitToRetryMilliseconds;

		/// <summary>
		/// This constructor is called by the NullTransport.
		/// </summary>
		internal ModbusTransport()
		{
		}

		internal ModbusTransport(IStreamResource streamResource)
		{
			Debug.Assert(streamResource != null, "Argument streamResource cannot be null.");

			StreamResource = streamResource;
		}		

		/// <summary>
		/// Number of times to retry sending message after encountering a failure such as an IOException, 
		/// TimeoutException, or a corrupt message.
		/// </summary>
		public int Retries
		{
			get { return _retries; }
			set { _retries = value; }
		}

		/// <summary>
		/// Gets or sets the number of milliseconds the tranport will wait before retrying a message after receiving 
		/// an ACKNOWLEGE or SLAVE DEVICE BUSY slave exception response.
		/// </summary>
		public int WaitToRetryMilliseconds
		{
			get
			{
				return _waitToRetryMilliseconds;
			}
			set
			{
				if (value < 0)
					throw new ArgumentException("WaitToRetryMilliseconds must be greater than 0.");

				_waitToRetryMilliseconds = value;
			}
		}

		/// <summary>
		/// Gets or sets the stream resource.
		/// </summary>
		internal IStreamResource StreamResource { get; private set; }

		internal virtual T UnicastMessage<T>(IModbusMessage message) where T : IModbusMessage, new()
		{
			IModbusMessage response = null;
			int attempt = 1;
			bool readAgain;
			bool success = false;

			do
			{
				try
				{
					lock (_syncLock)
					{
						Write(message);

						do
						{
							readAgain = false;
							response = ReadResponse<T>();

							var exceptionResponse = response as SlaveExceptionResponse;
							if (exceptionResponse != null)
							{
								// if SlaveExceptionCode == ACKNOWLEDGE we retry reading the response without resubmitting request
								if (readAgain = exceptionResponse.SlaveExceptionCode == Modbus.Acknowledge)
								{
									_logger.InfoFormat("Received ACKNOWLEDGE slave exception response, waiting {0} milliseconds and retrying to read response.", _waitToRetryMilliseconds);
									Thread.Sleep(WaitToRetryMilliseconds);
								}
								else
								{
									throw new SlaveException(exceptionResponse);
								}
							}

						} while (readAgain);
					}

					ValidateResponse(message, response);
					success = true;
				}
				catch (FormatException fe)
				{
					_logger.ErrorFormat("FormatException, {0} retries remaining - {1}", _retries + 1 - attempt, fe.Message);

					if (attempt++ > _retries)
						throw;
				}
				catch (NotImplementedException nie)
				{
					_logger.ErrorFormat("NotImplementedException, {0} retries remaining - {1}", _retries + 1 - attempt, nie.Message);

					if (attempt++ > _retries)
						throw;
				}
				catch (TimeoutException te)
				{
					_logger.ErrorFormat("TimeoutException, {0} retries remaining - {1}", _retries + 1 - attempt, te.Message);

					if (attempt++ > _retries)
						throw;
				}
				catch (IOException ioe)
				{
					_logger.ErrorFormat("IOException, {0} retries remaining - {1}", _retries + 1 - attempt, ioe.Message);

					if (attempt++ > _retries)
						throw;
				}
				catch (SlaveException se)
				{
					if (se.SlaveExceptionCode != Modbus.SlaveDeviceBusy)
						throw;

					_logger.InfoFormat("Received SLAVE_DEVICE_BUSY exception response, waiting {0} milliseconds and resubmitting request.", _waitToRetryMilliseconds);
					Thread.Sleep(WaitToRetryMilliseconds);
				}
			} while (!success);

			return (T) response;
		}

		internal virtual IModbusMessage CreateResponse<T>(byte[] frame) where T : IModbusMessage, new()
		{
			byte functionCode = frame[1];
			IModbusMessage response;

			// check for slave exception response
			if (functionCode > Modbus.ExceptionOffset)
				response = ModbusMessageFactory.CreateModbusMessage<SlaveExceptionResponse>(frame);
			else
				// create message from frame
				response = ModbusMessageFactory.CreateModbusMessage<T>(frame);

			return response;
		}

		internal virtual void ValidateResponse(IModbusMessage request, IModbusMessage response)
		{
			if (request.FunctionCode != response.FunctionCode)
				throw new IOException(String.Format(CultureInfo.InvariantCulture, "Received response with unexpected Function Code. Expected {0}, received {1}.", request.FunctionCode, response.FunctionCode));

            if (request.SlaveAddress != response.SlaveAddress)
                throw new IOException(String.Format(CultureInfo.InvariantCulture, "Response slave address does not match request. Expected {0}, received {1}.", response.SlaveAddress, request.SlaveAddress));
		}

		internal abstract byte[] ReadRequest();
		internal abstract IModbusMessage ReadResponse<T>() where T : IModbusMessage, new();
		internal abstract byte[] BuildMessageFrame(IModbusMessage message);
		internal abstract void Write(IModbusMessage message);
	}
}
