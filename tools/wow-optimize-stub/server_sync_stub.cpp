// wow-optimize's public source calls this from every hook wrapper but leaves out src/core/server_sync_compat.cpp,
// which defines it. Without that file its CMakeLists leaves HAVE_SERVER_SYNC_COMPAT off and nothing reads what this
// would record, so an empty definition is all the link needs.
#include <stdint.h>

extern "C" void WowOpt_RegisterServerSyncHook(uintptr_t, const void*) {}
