﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace ReportProcessingService
{
    public class ProcessPerformanceCounterException :Exception
    {
        public ProcessPerformanceCounterException(string message)
            : base(message)
        {
        }
    }

    public class ProcessPerformanceCounter : IDisposable
    {
        private PerformanceCounter counter;
        private readonly string counterName;

        public ProcessPerformanceCounter(string counterName)
        {
            this.counterName = counterName;
        }

        public float NextValue()
        {
            for (int i = 0; i < 3; ++i)
            {
                try
                {
                    return this.GetCounter().NextValue();
                }
                catch (InvalidOperationException)
                {
                    ResetCounter();
                }
            }

            throw new ProcessPerformanceCounterException($"Could not get performance counter for {counterName}");
        }

        private PerformanceCounter GetCounter()
        {
            if (counter == null)
            {
                counter = new PerformanceCounter("Process", this.counterName, GetProcessInstanceName());
            }

            return counter;
        }

        private void ResetCounter()
        {
            this.Dispose();
        }

        private static string GetProcessInstanceName()
        {
            Process p = Process.GetCurrentProcess();

            PerformanceCounterCategory cat = new PerformanceCounterCategory("Process");

            string[] instances = cat.GetInstanceNames();
            foreach (string instance in instances)
            {
                using (PerformanceCounter counter = new PerformanceCounter("Process", "ID Process", instance, true))
                {
                    if ((int)counter.RawValue == p.Id)
                    {
                        return instance;
                    }
                }
            }

            throw new InvalidOperationException();
        }

        public void Dispose()
        {
            if (this.counter != null)
            {
                this.counter.Dispose();
                this.counter = null;
            }
        }
    }
}
