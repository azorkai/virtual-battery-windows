/*
 * Virtual Battery - user-mode (UMDF 2) HID driver.
 *
 * Presents a HID Power Device (UPS usage) to Windows. The in-box, Microsoft
 * signed HidBatt.sys binds to it and exposes a normal system battery. This
 * file is the only custom driver code in the project and it runs inside
 * WUDFHost.exe, never in the kernel.
 *
 * The driver accepts no data from applications: the only tunable is one DWORD
 * in the registry key of the device, writable by administrators only.
 *
 * The battery always reports external power. A battery that claims to be
 * discharging can make Windows sleep or shut the PC down.
 */
#include <windows.h>
#include <wdf.h>
#include <hidport.h>
#include <hidclass.h>

#define REPORT_PRODUCT          0x01
#define REPORT_SERIAL           0x02
#define REPORT_MANUFACTURER     0x03
#define REPORT_RECHARGEABLE     0x06
#define REPORT_STATUS           0x07
#define REPORT_CONFIG_VOLTAGE   0x0A
#define REPORT_VOLTAGE          0x0B
#define REPORT_REMAINING        0x0C
#define REPORT_RUNTIME          0x0D
#define REPORT_FULL_CAPACITY    0x0E
#define REPORT_WARNING_LIMIT    0x0F
#define REPORT_GRANULARITY1     0x10
#define REPORT_LOW_LIMIT        0x11
#define REPORT_CAPACITY_MODE    0x16
#define REPORT_DESIGN_CAPACITY  0x17
#define REPORT_GRANULARITY2     0x18
#define REPORT_CHEMISTRY        0x1F
#define REPORT_OEM_INFO         0x20

#define STRING_MANUFACTURER     1
#define STRING_PRODUCT          2
#define STRING_SERIAL           3
#define STRING_CHEMISTRY        4
#define STRING_OEM_INFO         5

/* Present status bits, in descriptor order. */
#define STATUS_CHARGING         0x01
#define STATUS_AC_PRESENT       0x04
#define STATUS_BATTERY_PRESENT  0x08
#define STATUS_FULLY_CHARGED    0x20

/* The floor stays well above the low/critical battery levels of Windows. */
#define PERCENT_MIN             40
#define PERCENT_MAX             100
#define VOLTAGE_CENTIVOLTS      1200
#define RUNTIME_FULL_SECONDS    14400
#define POLL_PERIOD_MS          3000
#define KEEPALIVE_TICKS         10

static const UCHAR g_ReportDescriptor[] = {
    0x05, 0x84,                         /* Usage Page (Power Device)         */
    0x09, 0x04,                         /* Usage (UPS)                       */
    0xA1, 0x01,                         /* Collection (Application)          */
    0x09, 0x24,                         /*   Usage (PowerSummary)            */
    0xA1, 0x02,                         /*   Collection (Logical)            */
    0x75, 0x08, 0x95, 0x01,             /*     8 bits x 1                    */
    0x15, 0x00, 0x26, 0xFF, 0x00,       /*     Logical 0..255                */
    0x85, REPORT_PRODUCT,       0x09, 0xFE, 0x79, STRING_PRODUCT,      0xB1, 0x23,
    0x85, REPORT_SERIAL,        0x09, 0xFF, 0x79, STRING_SERIAL,       0xB1, 0x23,
    0x85, REPORT_MANUFACTURER,  0x09, 0xFD, 0x79, STRING_MANUFACTURER, 0xB1, 0x23,
    0x05, 0x85,                         /*     Usage Page (Battery System)   */
    0x85, REPORT_RECHARGEABLE,  0x09, 0x8B, 0xB1, 0x23,
    0x85, REPORT_CHEMISTRY,     0x09, 0x89, 0x79, STRING_CHEMISTRY,    0xB1, 0x23,
    0x85, REPORT_OEM_INFO,      0x09, 0x8F, 0x79, STRING_OEM_INFO,     0xB1, 0x23,
    0x85, REPORT_CAPACITY_MODE, 0x09, 0x2C, 0xB1, 0x23,
    0x26, 0x64, 0x00,                   /*     Logical maximum 100           */
    0x85, REPORT_GRANULARITY1,  0x09, 0x8D, 0xB1, 0x22,
    0x85, REPORT_GRANULARITY2,  0x09, 0x8E, 0xB1, 0x23,
    0x85, REPORT_FULL_CAPACITY, 0x09, 0x67, 0xB1, 0x83,
    0x85, REPORT_DESIGN_CAPACITY, 0x09, 0x83, 0xB1, 0x83,
    0x85, REPORT_REMAINING,     0x09, 0x66, 0x81, 0xA3, 0x09, 0x66, 0xB1, 0xA3,
    0x85, REPORT_WARNING_LIMIT, 0x09, 0x8C, 0xB1, 0xA2,
    0x85, REPORT_LOW_LIMIT,     0x09, 0x29, 0xB1, 0xA2,
    0x75, 0x10,                         /*     16 bits                       */
    0x27, 0xFF, 0xFF, 0x00, 0x00,       /*     Logical maximum 65535         */
    0x66, 0x01, 0x10, 0x55, 0x00,       /*     Unit (seconds)                */
    0x85, REPORT_RUNTIME,       0x09, 0x68, 0x81, 0xA3, 0x09, 0x68, 0xB1, 0xA3,
    0x05, 0x84,                         /*     Usage Page (Power Device)     */
    0x67, 0x21, 0xD1, 0xF0, 0x00, 0x55, 0x05, /* Unit (centivolts)           */
    0x85, REPORT_CONFIG_VOLTAGE, 0x09, 0x40, 0xB1, 0x23,
    0x85, REPORT_VOLTAGE,       0x09, 0x30, 0x81, 0xA3, 0x09, 0x30, 0xB1, 0xA3,
    0x09, 0x02,                         /*     Usage (PresentStatus)         */
    0xA1, 0x02,                         /*     Collection (Logical)          */
    0x85, REPORT_STATUS,
    0x05, 0x85,                         /*       Usage Page (Battery System) */
    0x65, 0x00, 0x55, 0x00,
    0x75, 0x01, 0x95, 0x01, 0x15, 0x00, 0x25, 0x01,
    0x09, 0x44, 0x81, 0xA3, 0x09, 0x44, 0xB1, 0xA3, /* Charging              */
    0x09, 0x45, 0x81, 0xA3, 0x09, 0x45, 0xB1, 0xA3, /* Discharging           */
    0x09, 0xD0, 0x81, 0xA3, 0x09, 0xD0, 0xB1, 0xA3, /* ACPresent             */
    0x09, 0xD1, 0x81, 0xA3, 0x09, 0xD1, 0xB1, 0xA3, /* BatteryPresent        */
    0x09, 0x42, 0x81, 0xA3, 0x09, 0x42, 0xB1, 0xA3, /* BelowRemainingCapacityLimit */
    0x09, 0x46, 0x81, 0xA3, 0x09, 0x46, 0xB1, 0xA3, /* FullyCharged          */
    0x09, 0x4B, 0x81, 0xA3, 0x09, 0x4B, 0xB1, 0xA3, /* NeedReplacement       */
    0x09, 0x47, 0x81, 0xA3, 0x09, 0x47, 0xB1, 0xA3, /* FullyDischarged       */
    0xC0,                               /*     End Collection                */
    0xC0,                               /*   End Collection                  */
    0xC0                                /* End Collection                    */
};

static const HID_DESCRIPTOR g_HidDescriptor = {
    sizeof(HID_DESCRIPTOR),             /* bLength                           */
    0x21,                               /* bDescriptorType: HID              */
    0x0110,                             /* bcdHID                            */
    0x00,                               /* bCountry                          */
    0x01,                               /* bNumDescriptors                   */
    { { 0x22, sizeof(g_ReportDescriptor) } }
};

typedef struct _DEVICE_CONTEXT {
    WDFQUEUE ReadQueue;
    UCHAR    Percent;
    UCHAR    Pending;       /* bit 0: status report due, bit 1: capacity due */
    ULONG    Ticks;
} DEVICE_CONTEXT, *PDEVICE_CONTEXT;

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DEVICE_CONTEXT, GetContext)

DRIVER_INITIALIZE                  DriverEntry;
EVT_WDF_DRIVER_DEVICE_ADD          EvtDeviceAdd;
EVT_WDF_IO_QUEUE_IO_DEVICE_CONTROL EvtIoDeviceControl;
EVT_WDF_TIMER                      EvtTimer;

static ULONG
QueryDword(WDFKEY Key, PCUNICODE_STRING Name, ULONG Default)
{
    ULONG value;
    return NT_SUCCESS(WdfRegistryQueryULong(Key, Name, &value)) ? value : Default;
}

static BOOLEAN
LoadSettings(WDFDEVICE Device)
{
    DECLARE_CONST_UNICODE_STRING(percentName, L"BatteryPercent");
    PDEVICE_CONTEXT ctx = GetContext(Device);
    ULONG percent = PERCENT_MAX;
    BOOLEAN changed;
    WDFKEY key;

    if (NT_SUCCESS(WdfDeviceOpenRegistryKey(Device, PLUGPLAY_REGKEY_DEVICE, KEY_READ,
                                            WDF_NO_OBJECT_ATTRIBUTES, &key))) {
        percent = QueryDword(key, &percentName, percent);
        WdfRegistryClose(key);
    }

    percent = max(PERCENT_MIN, min(PERCENT_MAX, percent));
    changed = ctx->Percent != (UCHAR)percent;
    ctx->Percent = (UCHAR)percent;
    return changed;
}

/* Returns the payload size in bytes, or 0 for an unknown report. */
static ULONG
GetReportValue(PDEVICE_CONTEXT Ctx, UCHAR ReportId, USHORT *Value)
{
    switch (ReportId) {
    case REPORT_PRODUCT:         *Value = STRING_PRODUCT;      return 1;
    case REPORT_SERIAL:          *Value = STRING_SERIAL;       return 1;
    case REPORT_MANUFACTURER:    *Value = STRING_MANUFACTURER; return 1;
    case REPORT_CHEMISTRY:       *Value = STRING_CHEMISTRY;    return 1;
    case REPORT_OEM_INFO:        *Value = STRING_OEM_INFO;     return 1;
    case REPORT_RECHARGEABLE:    *Value = 1;                   return 1;
    case REPORT_CAPACITY_MODE:   *Value = 2; /* percent */     return 1;
    case REPORT_GRANULARITY1:    *Value = 1;                   return 1;
    case REPORT_GRANULARITY2:    *Value = 1;                   return 1;
    case REPORT_FULL_CAPACITY:   *Value = 100;                 return 1;
    case REPORT_DESIGN_CAPACITY: *Value = 100;                 return 1;
    case REPORT_WARNING_LIMIT:   *Value = 10;                  return 1;
    case REPORT_LOW_LIMIT:       *Value = 5;                   return 1;
    case REPORT_REMAINING:       *Value = Ctx->Percent;        return 1;
    case REPORT_CONFIG_VOLTAGE:
    case REPORT_VOLTAGE:         *Value = VOLTAGE_CENTIVOLTS;  return 2;
    case REPORT_RUNTIME:
        *Value = (USHORT)(RUNTIME_FULL_SECONDS / 100 * Ctx->Percent);
        return 2;
    case REPORT_STATUS:
        *Value = STATUS_BATTERY_PRESENT | STATUS_AC_PRESENT |
                 (Ctx->Percent >= PERCENT_MAX ? STATUS_FULLY_CHARGED : STATUS_CHARGING);
        return 1;
    default:
        return 0;
    }
}

/* Writes a report into the output buffer of the request. */
static NTSTATUS
WriteReport(PDEVICE_CONTEXT Ctx, WDFREQUEST Request, UCHAR ReportId)
{
    PUCHAR buffer;
    size_t length;
    USHORT value = 0;
    ULONG size = GetReportValue(Ctx, ReportId, &value);
    NTSTATUS status;

    if (size == 0)
        return STATUS_INVALID_PARAMETER;

    status = WdfRequestRetrieveOutputBuffer(Request, size + 1, (PVOID *)&buffer, &length);
    if (!NT_SUCCESS(status))
        return status;

    buffer[0] = ReportId;
    buffer[1] = (UCHAR)(value & 0xFF);
    if (size == 2)
        buffer[2] = (UCHAR)(value >> 8);

    WdfRequestSetInformation(Request, size + 1);
    return STATUS_SUCCESS;
}

static NTSTATUS
GetReport(PDEVICE_CONTEXT Ctx, WDFREQUEST Request)
{
    /* mshidumdf passes the report id as the input buffer. */
    PUCHAR reportId;
    NTSTATUS status = WdfRequestRetrieveInputBuffer(Request, sizeof(UCHAR),
                                                    (PVOID *)&reportId, NULL);
    return NT_SUCCESS(status) ? WriteReport(Ctx, Request, *reportId) : status;
}

static NTSTATUS
CopyToRequest(WDFREQUEST Request, const void *Source, size_t Length)
{
    WDFMEMORY memory;
    NTSTATUS status = WdfRequestRetrieveOutputMemory(Request, &memory);
    size_t capacity;

    if (!NT_SUCCESS(status))
        return status;

    WdfMemoryGetBuffer(memory, &capacity);
    if (capacity < Length)
        return STATUS_INVALID_BUFFER_SIZE;

    status = WdfMemoryCopyFromBuffer(memory, 0, (PVOID)Source, Length);
    if (NT_SUCCESS(status))
        WdfRequestSetInformation(Request, Length);
    return status;
}

static NTSTATUS
GetString(WDFREQUEST Request)
{
    const WCHAR *text;
    PULONG input;
    NTSTATUS status = WdfRequestRetrieveInputBuffer(Request, sizeof(ULONG),
                                                    (PVOID *)&input, NULL);
    if (!NT_SUCCESS(status))
        return status;

    /* Low word: string id or index. High word: language id. */
    switch (*input & 0xFFFF) {
    case STRING_MANUFACTURER:
    case HID_STRING_ID_IMANUFACTURER: text = L"Virtual Battery"; break;
    case STRING_PRODUCT:
    case HID_STRING_ID_IPRODUCT:      text = L"Virtual Battery"; break;
    case STRING_SERIAL:
    case HID_STRING_ID_ISERIALNUMBER: text = L"VB0001"; break;
    case STRING_CHEMISTRY:            text = L"LION"; break;
    case STRING_OEM_INFO:             text = L"Virtual Battery"; break;
    default:                          return STATUS_INVALID_PARAMETER;
    }
    return CopyToRequest(Request, text, (wcslen(text) + 1) * sizeof(WCHAR));
}

/* Completes waiting reads while there are reports to deliver. */
static void
DeliverReports(PDEVICE_CONTEXT Ctx)
{
    WDFREQUEST request;

    while (Ctx->Pending != 0 &&
           NT_SUCCESS(WdfIoQueueRetrieveNextRequest(Ctx->ReadQueue, &request))) {
        UCHAR reportId;
        if (Ctx->Pending & 1) {
            reportId = REPORT_STATUS;
            Ctx->Pending &= ~1;
        } else {
            reportId = REPORT_REMAINING;
            Ctx->Pending = 0;
        }
        WdfRequestComplete(request, WriteReport(Ctx, request, reportId));
    }
}

VOID
EvtTimer(WDFTIMER Timer)
{
    WDFDEVICE device = (WDFDEVICE)WdfTimerGetParentObject(Timer);
    PDEVICE_CONTEXT ctx = GetContext(device);

    if (LoadSettings(device) || ++ctx->Ticks >= KEEPALIVE_TICKS) {
        ctx->Ticks = 0;
        ctx->Pending = 3;
    }
    DeliverReports(ctx);
}

VOID
EvtIoDeviceControl(WDFQUEUE Queue, WDFREQUEST Request, size_t OutputLength,
                   size_t InputLength, ULONG IoControlCode)
{
    PDEVICE_CONTEXT ctx = GetContext(WdfIoQueueGetDevice(Queue));
    NTSTATUS status;

    UNREFERENCED_PARAMETER(OutputLength);
    UNREFERENCED_PARAMETER(InputLength);

    switch (IoControlCode) {
    case IOCTL_HID_GET_DEVICE_DESCRIPTOR:
        status = CopyToRequest(Request, &g_HidDescriptor, sizeof(g_HidDescriptor));
        break;

    case IOCTL_HID_GET_REPORT_DESCRIPTOR:
        status = CopyToRequest(Request, g_ReportDescriptor, sizeof(g_ReportDescriptor));
        break;

    case IOCTL_HID_GET_DEVICE_ATTRIBUTES: {
        HID_DEVICE_ATTRIBUTES attributes;
        RtlZeroMemory(&attributes, sizeof(attributes));
        attributes.Size = sizeof(attributes);
        attributes.VendorID = 0x1209;   /* pid.codes */
        attributes.ProductID = 0x0001;  /* pid.codes private test PID */
        attributes.VersionNumber = 0x0100;
        status = CopyToRequest(Request, &attributes, sizeof(attributes));
        break;
    }

    case IOCTL_HID_READ_REPORT:
        status = WdfRequestForwardToIoQueue(Request, ctx->ReadQueue);
        if (NT_SUCCESS(status))
            return;
        break;

    case IOCTL_UMDF_HID_GET_FEATURE:
    case IOCTL_UMDF_HID_GET_INPUT_REPORT:
        status = GetReport(ctx, Request);
        break;

    case IOCTL_UMDF_HID_SET_FEATURE:
        /* Limits are fixed; accept and ignore. */
        status = STATUS_SUCCESS;
        break;

    case IOCTL_HID_GET_STRING:
    case IOCTL_HID_GET_INDEXED_STRING:
        status = GetString(Request);
        break;

    default:
        status = STATUS_NOT_IMPLEMENTED;
        break;
    }

    WdfRequestComplete(Request, status);
}

NTSTATUS
EvtDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT DeviceInit)
{
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_IO_QUEUE_CONFIG queueConfig;
    WDF_TIMER_CONFIG timerConfig;
    PDEVICE_CONTEXT ctx;
    WDFDEVICE device;
    WDFTIMER timer;
    NTSTATUS status;

    UNREFERENCED_PARAMETER(Driver);

    WdfFdoInitSetFilter(DeviceInit);

    /* One lock for queue and timer callbacks, so the context needs no other. */
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DEVICE_CONTEXT);
    attributes.SynchronizationScope = WdfSynchronizationScopeDevice;
    status = WdfDeviceCreate(&DeviceInit, &attributes, &device);
    if (!NT_SUCCESS(status))
        return status;

    ctx = GetContext(device);
    LoadSettings(device);
    ctx->Pending = 3;

    WDF_IO_QUEUE_CONFIG_INIT_DEFAULT_QUEUE(&queueConfig, WdfIoQueueDispatchParallel);
    queueConfig.EvtIoDeviceControl = EvtIoDeviceControl;
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, NULL);
    if (!NT_SUCCESS(status))
        return status;

    WDF_IO_QUEUE_CONFIG_INIT(&queueConfig, WdfIoQueueDispatchManual);
    status = WdfIoQueueCreate(device, &queueConfig, WDF_NO_OBJECT_ATTRIBUTES, &ctx->ReadQueue);
    if (!NT_SUCCESS(status))
        return status;

    WDF_TIMER_CONFIG_INIT_PERIODIC(&timerConfig, EvtTimer, POLL_PERIOD_MS);
    timerConfig.AutomaticSerialization = TRUE;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    attributes.ParentObject = device;
    status = WdfTimerCreate(&timerConfig, &attributes, &timer);
    if (!NT_SUCCESS(status))
        return status;

    WdfTimerStart(timer, WDF_REL_TIMEOUT_IN_MS(POLL_PERIOD_MS));
    return STATUS_SUCCESS;
}

NTSTATUS
DriverEntry(PDRIVER_OBJECT DriverObject, PUNICODE_STRING RegistryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, EvtDeviceAdd);
    return WdfDriverCreate(DriverObject, RegistryPath, WDF_NO_OBJECT_ATTRIBUTES,
                           &config, WDF_NO_HANDLE);
}
