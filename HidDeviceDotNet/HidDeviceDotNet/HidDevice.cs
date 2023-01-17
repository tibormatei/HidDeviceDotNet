#region Copyright (c) 2023 Tibor
//
// @author Matei Tibor
// @version 1.0
//
#endregion

using System;
using System.Runtime.InteropServices;
using System.ComponentModel;


namespace HidDeviceDotNet
{
    public class HidDevice
    {
        private IntPtr m_device_handle;
        private NativeOverlapped m_ol;
        private short m_output_report_length;
        private short m_input_report_length;
        private bool m_read_pending;
        private bool m_initialized;

        public HidDevice()
        {
            m_output_report_length = 0;
            m_input_report_length = 0;

            m_read_pending = true;
            m_read_pending = false;
        }

        ~HidDevice()
        {
            CloseDevice();
        }

        public void OpenDevice(ushort vid, ushort pid)
        {
            string path = SearchDevice(vid, pid);

            if (path != string.Empty)
            {
                uint desired_access = NativeMethods.GENERIC_WRITE | NativeMethods.GENERIC_READ;
                int share_mode = NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE;
                NativeMethods.SECURITY_ATTRIBUTES security_attributesl = new NativeMethods.SECURITY_ATTRIBUTES();

                m_device_handle = NativeMethods.CreateFile(path, desired_access, share_mode, ref security_attributesl, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

                if (m_device_handle.ToInt64() == NativeMethods.INVALID_HANDLE_VALUE)
                {
                    /* System devices, such as keyboards or mice, cannot be opened in
                       read-write mode, because the system takes exclusive control over
                       them.  This is to prevent keyloggers.  However, feature reports
                       can still be sent and received.  Retry opening the device, but
                       without read/write access. */
                    desired_access = 0;
                    m_device_handle = NativeMethods.CreateFile(path, desired_access, share_mode, ref security_attributesl, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                }

                bool res = NativeMethods.HidD_SetNumInputBuffers(m_device_handle, 64);
                if (res == false)
                {
                    NativeMethods.CancelIoEx(m_device_handle, IntPtr.Zero);
                    throw new Exception("SetNumInputBuffers error\n");
                }

                IntPtr pp_data = new IntPtr();
                res = NativeMethods.HidD_GetPreparsedData(m_device_handle, ref pp_data);
                if (res == false)
                {
                    NativeMethods.CancelIoEx(m_device_handle, IntPtr.Zero);
                    throw new Exception("GetPreparsedData error\n");
                }

                NativeMethods.HIDP_CAPS caps = new NativeMethods.HIDP_CAPS();
                int nt_res = NativeMethods.HidP_GetCaps(pp_data, ref caps);
                m_output_report_length = caps.OutputReportByteLength;
                m_input_report_length = caps.InputReportByteLength;

                NativeMethods.HidD_FreePreparsedData(pp_data);

                NativeMethods.SECURITY_ATTRIBUTES security_attributes = new NativeMethods.SECURITY_ATTRIBUTES();
                m_ol = new NativeOverlapped();
                m_ol.EventHandle = NativeMethods.CreateEvent(ref security_attributes, 0, 0 /*initial state f=nonsignaled*/, string.Empty);
                m_read_pending = false;

                m_initialized = true;
            }
            else
            {
                m_initialized = false;
            }
        }

        public void CloseDevice()
        {
            if (m_initialized)
            {
                NativeMethods.CloseHandle(m_ol.EventHandle);
                NativeMethods.CancelIoEx(m_device_handle, IntPtr.Zero);
                m_initialized = false;
            }
        }

        public uint WriteDevice(byte[] message)
        {
            uint bytes_written = 0;
            byte[] buff;

            if (m_initialized)
            {
                NativeOverlapped ol = new NativeOverlapped();

                if (message.Length >= m_output_report_length)
                {
                    /* The user passed the right number of bytes. Use the buffer as-is. */
                    buff = message;
                }
                else
                {
                    /* Create a temporary buffer and copy the user's data
                       into it, padding the rest with zeros. */
                    buff = new byte[m_output_report_length];
                    Array.Copy(message, buff, message.Length);
                }

                bool res = NativeMethods.WriteFile(m_device_handle, buff, (uint)buff.Length, out bytes_written, ref ol);
                /* Wait here until the write is done. This makes
                   hid_write() synchronous. */
                res = NativeMethods.GetOverlappedResult(m_device_handle, ref ol, out bytes_written, true/*wait*/);
                if (res == false)
                {
                    /* The Write operation failed. */
                    bytes_written = 0;
                }
            }

            return bytes_written;
        }

        public byte[] ReadDevice()
        {
            byte[] bytes = new byte[m_input_report_length];

            IntPtr ev = m_ol.EventHandle;
            byte[] read_buf = new byte[m_input_report_length];
            uint bytes_read = 0;
            bool res;

            if (!m_read_pending)
            {
                /* Start an Overlapped I/O read. */
                m_read_pending = true;

                // ResetEvent(ev);
                res = NativeMethods.ReadFile(m_device_handle, read_buf, (uint)m_input_report_length, out bytes_read, ref m_ol);
                if (res == false)
                {
                    if (bytes_read != 0)
                    {
                        m_read_pending = false;
                        bytes = new byte[0];
                        return bytes;
                    }
                }

                const int milliseconds = 100; // -1;  // ez majd parameterben kell legyen!
                if (milliseconds >= 0)
                {
                    /* See if there is any data yet. */
                    uint retCode = NativeMethods.WaitForSingleObject(ev, milliseconds);
                    if (retCode != NativeMethods.WAIT_OBJECT_0)
                    {
                        /* There was no data this time. Return zero bytes available,
                           but leave the Overlapped I/O running. */
                        bytes = new byte[0];
                        return bytes;
                    }
                }

                res = NativeMethods.GetOverlappedResult(m_device_handle, ref m_ol, out bytes_read, false/*wait*/);

                /* Set pending back to false, even if GetOverlappedResult() returned error. */
                m_read_pending = false;

                if (res && bytes_read > 0)
                {
                    bytes = new byte[bytes_read];
                    Array.Copy(read_buf, bytes, read_buf.Length);
                }
            }

            return bytes;
        }

        protected string SearchDevice(ushort vid, ushort pid)
        {
            string path = string.Empty;

            Guid interface_class_guid = new Guid();
            NativeMethods.HidD_GetHidGuid(ref interface_class_guid);

            /* Initialize the Windows objects. */
            NativeMethods.SP_DEVICE_INTERFACE_DATA device_interface_data = new NativeMethods.SP_DEVICE_INTERFACE_DATA();
            device_interface_data.cbSize = Marshal.SizeOf(device_interface_data);

            /* Get information for all the devices belonging to the HID class. */
            IntPtr device_info_set = NativeMethods.SetupDiGetClassDevs(ref interface_class_guid, String.Empty, IntPtr.Zero, NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);
            if (device_info_set.ToInt64() <= 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            /* Iterate over each device in the HID class */
            NativeMethods.SP_DEVINFO_DATA devinfo_data = new NativeMethods.SP_DEVINFO_DATA();
            devinfo_data.cbSize = Marshal.SizeOf(devinfo_data);

            bool res = true;
            uint device_index = 0;
            const byte MAX_USB_PORTS = 127;

            for (byte i = 0; i < MAX_USB_PORTS; ++i)
            {
                res = NativeMethods.SetupDiEnumDeviceInterfaces(device_info_set, IntPtr.Zero, ref interface_class_guid, device_index, ref device_interface_data);
                if (res == false)
                {
                    /* There are no more devices. */
                    break;
                }

                uint required_size = 0;
                /* The function returns size and details about a device interface. The size is put in &required_size. */
                res = NativeMethods.SetupDiGetDeviceInterfaceDetail(device_info_set, ref device_interface_data, IntPtr.Zero, 0, ref required_size, IntPtr.Zero);

                NativeMethods.SP_DEVICE_INTERFACE_DETAIL_DATA device_interface_detail_data = new NativeMethods.SP_DEVICE_INTERFACE_DETAIL_DATA();
                device_interface_detail_data.cbSize = IntPtr.Size;

                /* Get the detailed data for this device. The detail data gives us
                   the device path for this device, which is then passed into
                   CreateFile() to get a handle to the device. */
                res = NativeMethods.SetupDiGetDeviceInterfaceDetail(device_info_set, ref device_interface_data, ref device_interface_detail_data, required_size, ref required_size, IntPtr.Zero);
                if (res == false)
                {
                    device_index++;
                    continue;
                }

                uint desired_access = 0;
                int share_mode = NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE;
                IntPtr hid_handle = new IntPtr(NativeMethods.INVALID_HANDLE_VALUE);
                NativeMethods.SECURITY_ATTRIBUTES security_attributesl = new NativeMethods.SECURITY_ATTRIBUTES();

                hid_handle = NativeMethods.CreateFile(device_interface_detail_data.DevicePath, desired_access, share_mode, ref security_attributesl, NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                if (hid_handle.ToInt64() == NativeMethods.INVALID_HANDLE_VALUE)
                {
                    NativeMethods.CloseHandle(hid_handle);
                    device_index++;
                    continue;
                }

                /* Get the Vendor ID and Product ID for this device. */
                NativeMethods.HIDD_ATTRIBUTES attrib = new NativeMethods.HIDD_ATTRIBUTES();
                attrib.Size = Marshal.SizeOf(attrib);

                NativeMethods.HidD_GetAttributes(hid_handle, ref attrib);

                if ((attrib.VendorID == vid) && (attrib.ProductID == pid))
                {
                    path = device_interface_detail_data.DevicePath;

                    NativeMethods.CloseHandle(hid_handle);
                    break;
                }

                NativeMethods.CloseHandle(hid_handle);
                device_index++;
            }

            /* Close the device information handle. */
            NativeMethods.SetupDiDestroyDeviceInfoList(device_info_set);

            return path;
        }
    }
}
