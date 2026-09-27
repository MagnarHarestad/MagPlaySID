//The mother of all laziness

if def _ALL_INCLUDED == 0
const _ALL_INCLUDED = 1

include "6510.s"
include "6510illegal.s"
include "word.s"
include "utils.s"
include "vic2.s"
include "irq.s"
include "longbranch.s"
include "startup.s"
include "colors.s"
include "sid.s"

endif //_ALL_INCLUDED