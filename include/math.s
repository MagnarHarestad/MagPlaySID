if def _MATH_INCLUDED == 0
const _MATH_INCLUDED = 1

/* a small macro that calculates SIZE sinusvalues between LO and HI with frequency FREQ */
	
macro m_calcsinus size:16, lo:8, hi:8, freq:8 {
	lua {
		local i, s;	
		local l = ##lo
		local h = ##hi
		s = 0
		for i=1, ##size, 1 do
			asm("byte ",math.floor((math.sin(i*##freq*2*math.pi/##size) * (h-l-1) / 2 + ((h-l)/2))+l))
		end
	}
}

macro m_calcsinus size:16, lo1:8, hi1:8, freq1:8, lo2:8, hi2:8, freq2:8 {
	lua {
		local i, s, v1, v2;	
		local l1 = ##lo1
		local h1 = ##hi1
		
		local l2 = ##lo2
		local h2 = ##hi2
		
		
		
		s = 0
		for i=1, ##size, 1 do
			
			v1 = math.floor((math.sin(i*##freq1*2*math.pi/##size) * (h1-l1-1) / 2 + ((h1-l1)/2))+l1)
			v2 = math.floor((math.sin(i*##freq2*2*math.pi/##size) * (h2-l2-1) / 2 + ((h2-l2)/2))+l2)

			asm("byte ", v1+v2)
			
		end
	}
}

macro m_calcsinus size:16, lo1:8, hi1:8, freq1:8, lo2:8, hi2:8, freq2:8, lo3:8, hi3:8, freq3:8 {
	lua {
		local i, s, v1, v2, v3;	
		local l1 = ##lo1
		local l2 = ##lo2
		local l3 = ##lo3
		
		local h1 = ##hi1
		local h2 = ##hi2
		local h3 = ##hi3
		
		
		
		s = 0
		for i=1, ##size, 1 do
			
			v1 = math.floor((math.sin(i*##freq1*2*math.pi/##size) * (h1-l1-1) / 2 + ((h1-l1)/2))+l1)
			v2 = math.floor((math.sin(i*##freq2*2*math.pi/##size) * (h2-l2-1) / 2 + ((h2-l2)/2))+l2)
			v3 = math.floor((math.sin(i*##freq3*2*math.pi/##size) * (h3-l3-1) / 2 + ((h3-l3)/2))+l3)

			asm("byte ", v1+v2)
			
		end
	}
}


/* Same same, but different. Use MATH.COS() instead... */

macro m_calccosinus size:16, lo:8, hi:8, freq:8 {
	lua {
		local i, s;	
		local l = ##lo
		local h = ##hi
		s = 0
		for i=1, ##size, 1 do
			asm("byte ",math.floor((math.cos(i*##freq*2*math.pi/##size) * (h-l-1) / 2 + ((h-l)/2))+l))
		end
	}
}


endif //_MATH_INCLUDED