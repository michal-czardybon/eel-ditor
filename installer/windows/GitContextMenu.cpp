#include <windows.h>
#include <shobjidl.h>
#include <wrl/client.h>
#include <memory>
#include <new>
#include <string>

namespace
{
    // Matches the CommandStateHandler registered by EelDitor.iss.
    constexpr CLSID GitMenuClass = {0x97a08ef1, 0x70ed, 0x467c, {0xa5, 0x25, 0x68, 0x19, 0x27, 0x45, 0x3c, 0x90}};
    long moduleReferences = 0;

    template<typename Interface>
    class ComObject : public Interface
    {
    public:
        ComObject() { InterlockedIncrement(&moduleReferences); }
        virtual ~ComObject() { InterlockedDecrement(&moduleReferences); }

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override
        {
            if (!result) return E_POINTER;
            *result = nullptr;
            if (iid != __uuidof(IUnknown) && iid != __uuidof(Interface)) return E_NOINTERFACE;
            *result = static_cast<Interface*>(this);
            AddRef();
            return S_OK;
        }

        ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }

        ULONG STDMETHODCALLTYPE Release() override
        {
            ULONG count = InterlockedDecrement(&references);
            if (count == 0) delete this;
            return count;
        }

    private:
        long references = 1;
    };

    class GitMenuState final : public ComObject<IExplorerCommandState>
    {
    public:
        HRESULT STDMETHODCALLTYPE GetState(IShellItemArray* items, BOOL allowSlow, EXPCMDSTATE* state) override
        {
            if (!state) return E_POINTER;
            *state = ECS_HIDDEN;
            DWORD count = 0;
            if (!items || FAILED(items->GetCount(&count)) || count != 1) return S_OK;
            // Explorer can retry off its UI thread, including for folders on a network drive.
            if (!allowSlow) return E_PENDING;

            Microsoft::WRL::ComPtr<IShellItem> item;
            if (FAILED(items->GetItemAt(0, item.GetAddressOf()))) return S_OK;
            PWSTR rawPath = nullptr;
            HRESULT result = item->GetDisplayName(SIGDN_FILESYSPATH, &rawPath);
            std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)> path(rawPath, CoTaskMemFree);
            if (FAILED(result) || !path) return S_OK;

            try
            {
                std::wstring marker(path.get());
                if (marker.empty()) return S_OK;
                if (marker.back() != L'\\') marker += L'\\';
                marker += L".git";
                // A linked worktree has a .git file in place of a .git directory.
                if (GetFileAttributesW(marker.c_str()) != INVALID_FILE_ATTRIBUTES) *state = ECS_ENABLED;
                return S_OK;
            }
            catch (const std::bad_alloc&)
            {
                return E_OUTOFMEMORY;
            }
        }
    };

    template<typename Object>
    HRESULT CreateObject(REFIID iid, void** result)
    {
        auto object = new (std::nothrow) Object();
        if (!object) return E_OUTOFMEMORY;
        HRESULT status = object->QueryInterface(iid, result);
        object->Release();
        return status;
    }

    class GitMenuFactory final : public ComObject<IClassFactory>
    {
    public:
        HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** result) override
        {
            if (!result) return E_POINTER;
            *result = nullptr;
            return outer ? CLASS_E_NOAGGREGATION : CreateObject<GitMenuState>(iid, result);
        }

        HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
        {
            if (lock) InterlockedIncrement(&moduleReferences);
            else InterlockedDecrement(&moduleReferences);
            return S_OK;
        }
    };
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** result)
{
    if (!result) return E_POINTER;
    *result = nullptr;
    return clsid == GitMenuClass ? CreateObject<GitMenuFactory>(iid, result) : CLASS_E_CLASSNOTAVAILABLE;
}

STDAPI DllCanUnloadNow()
{
    return InterlockedCompareExchange(&moduleReferences, 0, 0) == 0 ? S_OK : S_FALSE;
}
