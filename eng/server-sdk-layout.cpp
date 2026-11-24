// Check compiler-emitted Itanium member pointers against the audited Linux ABI.
// Does not construct an engine object, install hooks or invoke engine functions.
#include "engine2/isource2server.h"
#include <cstddef>
#include <cstring>
#include <iostream>

template<class M> bool CheckSlot(const char* name, M method, std::ptrdiff_t expected)
{
    struct Member { std::ptrdiff_t address, adjustment; } value{};
    static_assert(sizeof(method) == sizeof(value), "Unexpected member pointer ABI");
    std::memcpy(&value, &method, sizeof(value));
    if (value.address == expected * std::ptrdiff_t(sizeof(void*)) + 1 && value.adjustment == 0)
        return true;
    std::cerr << name << ": unexpected member pointer " << value.address
              << ", adjustment " << value.adjustment << "; expected slot " << expected << '\n';
    return false;
}

int main()
{
    bool ok = true;
#define CHECK(method, index) ok = CheckSlot(#method, &ISource2Server::method, index) && ok
    CHECK(WriteSignonMessages, 15);
    CHECK(PreWorldUpdate, 16);
    CHECK(GameFrame, 20);
    CHECK(ServerHibernationUpdate, 23);
    CHECK(IsPaused, 32);
    CHECK(GameServerSteamAPIActivated, 42);
    CHECK(GameServerSteamAPIDeactivated, 43);
    CHECK(OnHostNameChanged, 44);
    CHECK(PreFatalShutdown, 45);
    CHECK(UpdateWhenNotInGame, 46);
    CHECK(GetEconItemSystem, 47);
#undef CHECK
    if (!ok) return 1;
    std::cout << "PASS: 11 audited ISource2Server Linux slots\n";
}
