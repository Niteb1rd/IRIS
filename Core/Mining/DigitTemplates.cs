namespace Iris.Core.Mining;

public static class DigitTemplates
{
    public const int Width = 14;
    public const int Height = 20;

    private static readonly string[] Encoded =
    {
        "0:ERITFRkdKCokHRYPExYSExk1U3B7fntdPBcSExMZKWeez9nd3a1zLRgPFS5Zrtnn6Oru3rdsNA8aSYzT5dWtqcnf1p5QFSFkuePcsVlOks3lx2sgJW3F6NagRzp5xOzZfC0mcMnmzpRANHK/6Np/MiZwyeHIjj00c7rg0nowKnDE2cCHOzNytNjJdC0scMLWvIU6NHKy1Md0LSxwwtW7hDo1dLLTx3QuLXDA1LuGOzZ2stLFcSosbLrRvo1IRIG2z71qJCVjr87Enmdnnb/Jrl0aJFCMwMu7o6S+yLmEQhEjPGKfvcbHyMm6llQoCyMoNF6Grba1qX1QJBYRHh4hM0hdY2JbPyYVExMYGBgZGRkaGhkRDRASFQ==",
        "1:DQ4QFhsfHx8fFxIPEBEOHDJRZnN2bFUyGA4PEBAxYJq+1NnEk1AgDg4QEj58vuDt79WfViMQEBIVM1+Tutfr2Z9XIxEUGRYgMUt5suHanlckERUbERMYLmGk2tidViMQERQTExYnWZ7W1JlTIQ8QFBQWGitamtDOlFAgERMYFhgeM1yRxcWQUCMUGR8YGh80WYq7vIxPJBYcIx4eIjZbjLu7ik0jFx0jHh4iN1yMurmKTiQaHyQXGiI6X4u0tIpSKx0eIRAdMU1ukrS0k2Y+HhcXFDFXfZmvvr6vlHBCJhQcP26atMDCwr+xk10zFh06Y46kra+wr6CEVjEYFiU7T1ldW1tdVUYwHhIPEhYWFhYQDxMTERAODQ==",
        "2:CwoJCw8UFRQRDg0QDQsKDRUyT2pubGRNNBwQCQoVLGugz9PQxJ1vOBkHEy1as9zk4N7e2r+AOAIaUJjZ696uobjg5bRQAR9y1enhxGFHd8r02mQBF2fI5MN7NzBkvvLmbgcWRoGZfUIlNnXD8OZvCRkXFyIhGylWoNTt3WoJFhkocZSeo7fY6eGyWBESKVOv2uTe3+bVsnQ6Eg1Il9zz69nOzJphIhELEWPI7uCxc1ZbSTQaEg8Wduj404w3EBgaGhYUExx/8vjOhjgUGRsaFBUWGXzv+OCyiHmGgXZjNxMYeOr479rOztrTw6hVERl45/P07u3r5ubeyWURFUR9hIR/gYOFhX9wOw8QFh0gHRYaISsqJRsTDQ==",
        "3:GxoaGhoaGhYPEhYdHh4VFRs+Xnp+gIBvVC4fFw8TJXKy5+/2/tmjVSoRESZQtOX0+fv77ti3XhYXQ4TY8+m2pbXb8OeAJx9fsubqy1UuV7f2+5Y+FkqPzcCDPS9asu74mkYSMVuFgF9da4jD6eqKNhMXHB8zVqTP1ePl02wWGBkaH0B1vuLj5tWkUREaGhsiPWeky97j1KlXFhkbHigrLVaKx9ni4XwkG0BxkYVdRFKHxevsiTQcWqnTw4xBMmK37e+NOB1iuNnVtk8zYbDk6oQrGk+Xz+DVpZanyNvTcBoXOWy02OXl4tzXxqJRDxYiO36y3Ofp48ybRCAOGx0lSGN3enp3Z0ocEhAfHBkiIh4WExQNCQkOEg==",
        "4:DAsLEA8JDBUnJB4UExMNDQ0PDxErTHNyXCkYEBAREQ8SHFiV0c+nSSEMFhcZFh4wicr08clsMQwbGxwaNWS05vn3z2wxDB0cHCJXp9by+/nOZSwLHBkcRobS5PL8+M5oLwsWGyppqeXX2+/ux2QuDRooRZPF4rm44OTAYy4NIkd6r72wjJjU2bplLw0tYKC+tZN7j87RtGs0Dzx2usa3mput0c+5h0gZTYG/yca7vMTS0cOkYCpNdqa1uLS3wdHRw6JfKjRIY3N0a3qbzc60dDoQJS05QkA6WIjLza5gLAkdHiAjIiFFfMbIplIkCR4cGh4eHjtssrSURR0GFxcYGhkXJ0NpaFUoFQsRExYXFREXHykmHhAPEA==",
        "5:ERMUFxcWICAXGx4eFxESKkhUWFdeXVVXV1QxExVOlKmwr7KwqqmnoVYWHHTc7/Tx8O7q6eXddRwffOr38uHKwcW9tKhbGSaC7vnitXheZ11VUTIYJ4bz+dORPxcXEhAWFRQYd+Xw2rB9YV5HMRsTDxht0d/czbuzs594NR4TH12owcbAwcTLybB0PRYrSnKMkIiIk6e1tJ5QEC40PEVEPDdMe56yrVcNK0FbZFc8KTlum7SxVwojW52lkmw8QHiju7VXBiVotburjVlaka67tFYFKVqXsrWpj5GuurabSQYqRWybsLS0trq4pXU2Bik0R3GRqa+xsJZsLBcPIiYtP09fYmJgUDgTEhYbGxoWFRUTEQ8QEhEXHA==",
        "6:FBIRERYdJCgoKysmGhAPEx0/XHV6fX2AeWU5FQsaN4G23N7f3t/TsFwZETlzwOf18O3q6t6+XhAYWKre6NSok5SWjXI8ER9uzuzXoEYcHyIfExMUGWzR7M+PSygmIRoTFBYVa9Ho1quFb2lcRyYaFBdpzN/g1c/LybePRiUTG2rI2NrUycbMzbNwNg0gasHQxquNjKvHxJFHDiNsws2weTk7fbfNqlURKm/CzK51MTWAuM+1XBMrbbzLrnYwNoW5zbRcEyNgq8GvgkJEiLbGpVMQHVOWuL2tj46rwryIQQobRX2pwcvFw8XFqWMuChgpRXmgvL27tqF3KxYPGR8qRVttbmphVT8XEhMbHB0fIycnIhgYFhATFg==",
        "7:HBgUFxYRDQwPEhUXFRIZIjJVYWBiYV1fX106HBg0X6G8vsbGvLy6tGstJFea0OPg6+/t6+rrlkwlR3OXpKKms8rh8fOaTSEhIjI5OzZOhcHl4Ig8GhsdHh8iI0qY0ubCaSEYFxcXFxgjWrzi4KNQEhoVERkbGjN57PHShD0LGhcVGBodWJ7v47diLg4YFxYZGh94ve/Tmz8dDRITFhscIJPX7MB9GwoJFBMSEyA5r+vtrGUZCAYUExIQKlnF9emPQxMKCRETFRQ9gtPy32UTCQ4UFhQSD0KW2uW3TQkJDhIXEw4OSKTf2pc9BwoNDg8NDhhUrN3SizkICwsLEg8METVqjYVSJAgKDA4WEQoJFy1FQB8RCQkNEg==",
        "8:EA4MER8wNzUpHRUSEhMSHTFdeo2Qj4dyUyYXERM0aLng7evs8NOeRiEPElqz3uHNxNDx6cV3OQ4Xac7q0JZ7kNbp251MDh5uz+q5WyxKs9/mt1kPIm7M67xgN1W7396oUAwfXrDhzZB7j87cyYQ+ChdEhdLo3eTo6dirVSYLGlSh1+PW1Nnk1qtTJw0cYbbZ0KyeqtDTtWYxDh9pwtetXUFcr8/KkEUMKnLI2qZJLE+z0tCiUhQsdMrcrFU3WrzV0KFSFSFrxd3Ch2uBydnHhT8MIl2p1dfAtb7a2LVjLQojSX210Nnb2dO+kUAeDB4pPGqUubasmndPIBQRHB8kNU1oY1tOOykWExQbGxoSGCUfGRMSERIUFQ==",
        "9:ExgdHSUwNSYCExwSEBAKGDBee42OgWZUOxYPDQMkWK/f9fDm16ZsJRELFWG/4vX88uvn47pXJwsffu721JqOpuL13otCDyWJ/fmfEwdH0/HsulwTKov8/KETCkXG6/DIXgcriPT3nxULRsXq7sZdCCqB5u6eHBFLzOvouFoQHnnj7NGimbDp9+m0WRESWLDW5ujk5/P667dYDgcgTKvc7fHw6/ftv1kHDxkpRVxtZ4rW7OKrUw4OEhULEB8WT8zo3Z9NDgEJEQ8UHRtX0u7jokgEJFSLipKdnbTk89VxMgk8i+Xl7Pn58+jZqD4dEDWH5uPn8O3iz4ZFFRIXIE1/e3p6e3JhPiEPERcPGCIdFxIVDwELEhATFQ==",
        "0:CwoKCQoLExURDQoKCgsQFyI4T2dtcG5eSSwbEBcuUHuctLO1u66SYzcYHEaAtMjGt7nN08acVh8iYK3M08mUkL3T2styJyhxx9vWvmdfpM3l4oQyK3jT4dW0TUKUy+vrjDoqedXi1LBMQ5XM7O2OOyt51uPUr0xDlszs7Y47LHrW49SuTESWzOzsjTste9fk1K9MRJbM7OyNOy581+TVr0xEl8zs7I07MH3X5NWxTkaazuzsjDo0fNPi17pbVKXU7eqJNi9ywtvdzo6JwOHw44AsKmCl0eHey8zk7ebGaRwpSHe62uLk5ujjxH0/FCo4T3mXrsPCrI9qOiYcISYuPU5fcnFaQSsaGx4WFhYWFhYZFxAODxMWGQ==",
        "1:BwgJCQoLDhARDw0NCwouIxgeOmF1gYNwThQSGVRALEV9xdnk5sqMHB8xY007a6nq6+vp0ZMaMVVnTzhVhsHe7OzUlBg6amlQNDhTfsLo7teXGEB1a1IzJSQvn9/v2poaPnBpUDEkISqe4O/ZmRk6aWRLLSIgKp7f7tiYGTxtXUUnISAqnt/t2JgZPXBSOR0eISyf4O7ZmBk9b0QxGyAkLqHi8NqZGT5xOSscIiYvo+Ty25oaP3M1Kh0kJy+k5fPcmho9b0YzHiMmL6Tm9NyaGTppWUElJCUupOb13ZoYMFdrUTInJC2k5/bemxgmRGlQMSUiLKDj9tWRFx41STglIB8kYYSOdk8THjAjHRYaHB0ZFRMMCQ8iMg==",
        "2:BwYGBgYGCAgHBwcJCQgMEhofOV5hY2RGLSIUChUoQ2OMuKuuv6B8VSsMFjRirc/VtrbV28aJQQ0WUZ3E09CUltfg17dWBxhUnr/Ft2VryN7l2mcGGlWeurypTlGw2OneaAQaQ3ien4ZVaMLg5cldAxwrQFZYTleE1unco0wJHSMsMzhBf7jq7Mt2OhMcIywyR2iy3uvVpE0uHxsiLD5noNHn46xwMCcpGyEvWY/K5uXJdTgiKzYbKUN/uO3u0ZZWLCk1QBtAdbHX7+7FdlNDTkpFGWC31ufv7tShlpahc0oXaMrm8O/z8Oft8/eXRB1nwdzl4+fn4+jr6449HkRygYJ7fX16f4GATyUbHR8cEwYHBwUJCwoLDA==",
        "3:CQoKCAgLDAoHCAoODg0LL1xxf4eGg3yAgoNBBwtJl7vO1tzXyNTb2GUCC0iUtsTF2dW50N7YZAAJHkB4g3awxrfP0rFQARARFSk2RKTMvc+4Wy4WDgwKDSxfvNzAs4MaGywGBwsTVbnW2L5+RRgnOwkKDRRcydjVwK2BLCw4CQoNDj6Ip7i8z8WMUScMDg8NEyA/cbPS39VsEhIZIRoXFiFYutrq520EFEuHeHmBRVi64PXxcAIkd9bQ0c9TSbDe9/NxACp1zNLX1W5is9/28G8AJ2CnytrdvrbE5e/SYQIqPGC43+fy7dnu0WAqDC4zQXegwdbMopp4KBwdMjI2SV92j4ZZTj0iJSw1NTY2NjlLSC4yNTY2Ng==",
        "4:BgYGBQcJCwwNDAoHCQwKDRMoSnJxXjofDxETFRUaKl6Y09CmUygUHyIjJDNQj7zd1p83GhImLC8pQmuv1OPTkh8bJDpBRDRVh8jk58J9GiQ2SE1PQ2uh0+Xgp2ssPk1QUVJOfrrV2s2EaHeSkGFSTFSN0NzVwHNxu828fVU8Y57i49O1c4To79mdWSaBsenm0K6AlOnw3qxWEqTE6eXcz8PN6+/q13got8/r5+bo8fTv8fLxjDWoyO3n5+n09vL18ueBKWN/nZOMiZ/B7vXmulwRL0FTSUREa6Ps9NqSRAsnMj46PUZyqevy1oZCEywzOzg7RGui6O3Pf0AVLDAzMDM5TmyRjnhIKxkrKysoKi41NC0jFwsUHg==",
        "5:Dg8QEBESFBQREhIQDQsRMFp7iYuKiYeJgWk0ChVQnNDl5+Xj4+XWrlIIHV+y2+3u7Oro6dmvUAQgYrXd49CPb29xa1gtCidqveXlxVAaIysuKSMfK2y+5ubFTRovNjcvLzAjZLbb38uKepuFYzIpKB9jt9rk3s3M2s2oYjcdIma73evs5uXo6tSZSxEoaLjg7ejJyejt4LhXCSxXkL6+nmVwv+Dp0GYOK0Bfi4JXLECV0vHjdRcuTnurq4pLUZ7X9Oh4GSpbntXm2YuDwuby224SIlKV3fv70Mjj8uzGXQgcOm3S+/38+/r44alNBR0sSoq74Onp4sWUSCQQHSQxSmeGkpCAZUMZFhkcHyIiJCgtJhMNDBIaIA==",
        "6:CQkKCQ8ZKi0iEggICAcKIT1QYXF1eHloUTAYCA4+fafBz7q81cmqcTQIEk2b1Onltbbo6NeuUgkXYb3k8emPjOHs5cljDh5v0evw43hy0eTgvmATI3PS5+bVf3W3zceWSxIebs7j5ty7uNPKq246Fh9w0OTo4tbX5tSzfUIXH3LU6OzmyMno59i1WxIhdNjt8+qVkt/s69lqDCV32vD05mFb0ev17nUOJnja8PLgUUnH6Pn3eg8iddjw8t9YUMfo+fh7DyBty+z16X131+7593gLH1ml4PbxuLXp9PPibAgbO3DK8PPx8vT13qNMCiEsRZLD4ebm38ucQSMXJCcxV3SKlZF8aEsdHiUjJCYoKi07MxMMDBUkMA==",
        "7:CAcGCgsLCw0QDgwNDAsIIkdxgoODg4GEgXZJJAo8f7/a39za2d/e0300D0iTxt/n2tjh6e3thSoLQorBy7eNjrnd7+h2FAsvX4R7Vjxct9/nw1wIDBssLSMUKmvX7d2XRQgLDQ8NEyBVmez20m8yCwsMDw8XKHa58vfJUSYSCwwPERosh8r08748IBsKDA4RHjSPz/TbmyQZIQoMDhElR6Xf9rBkGBomDAwNETJovu33izgUHiwUEA0TSp/W8/V/KxcgKy8fDh1ixubr12khGyMsMiAQLnjY7NicShkeJSsdFBJFkOTwyXA0FB4lKRMPEkOI1dutSyYWIicqFBARLVWCf2IsHRkkKCkcFg8QFRsQDhYZHSImKQ==",
        "8:CgcEBggMJTEyGQgGCAsNDxY4VGt7hIRjRCwYCw8cNXSctL7Dwq2NXS0KFTBdqcfJ0tXR2sqQQgcfSIG8z8ixp6vP1qtQBy9hos/TvHRmk83iulYFOWmn1dm/bmKb1OfAWAQ4XI/O39K3ts/n5r1YBjVSf8jk4uXp7vXnt1kOK0p6wuDj7vLx9d6dURghRXrB3N7h4uDs3KBUGjlYh8vf1bGnud3juVwSSm+i1Nm9YlaX0+jCXAk9bKrY3MFuYpzW6sVdCSpbnNXi0aWhxubqwlsIIEqGzObj2tzq8uS1VgoeQXW01eLs8O7nyIJCFSA2U22RusfFsp96Ni0vJTA8P1NygH1pVj8fL0EtMTYwLSw3OTQtKy87RQ==",
        "9:CQoKBRAlNjozHQ0ICw0JChE3UmaAgmpVORIMDAkTLX6mtMbEraJ8KhMMCTFtu93j4NzW27xgLAsIY87a4+e0rtbi3sJaBApz6+bo5nNu1ujs4WkDDXbt6eniVVLb7/TucAURd+vp6eNMS9/0+fNzBB533+To5mFg4vL28HACIHDP3untmZfo8vPubgEdUZfQ7PPa2O/29u9wBBgwV5vN8PTy7PX48HEFICg6cK3q2tnl8/jxcgUlVJHB3umFguHz+vRzBCJqwt3s7Xp45vb58XEDFWPB3u7zp6Ts9/TjaQIEOYPQ9P759fP47clcAgEcSp/X+vr27eG1VikQBxQsY4ihoZuQhGAWFB0SGyUoKikoIBIMCw8cJg==",
        "0:CwoKCgwQFBURDQsKCwsMDhY3WXuEhH1ePBgPDQ4ePHurztXW0LCBPyAOEj17wd/i3d3i4cWAPg8YXrTa49efnM7g3LheFSNz0+jdu19bsNnp1nMfKX3g7timQT+h1e/jfCUqfuDu1qJBQaHV8OV9Jit+4O7VoUFBodXv5H0lK37g7tWgQUGi1e/kfSUrfuDu1aBBQaLV7+N9JSx/4e7VoEFCo9bv5HwlLYDi79WgQUKk1+/kfSUsf+Hu1qRERaja8eN7IyR01enevG9wwOTw2XIcGWC54evhwMLl8ee8XhEVQ4XO7vLv7/Tvy3c2ChgrSoSuzNbTxKBwMRoQFxslP1t3g39qSCkPEBUSExUUFxsfHBELCQwRFA==",
        "1:CgkJCAoMDxITEQ8ODAopHRIcNlpzgYRiPRUWHEYyIkZ6tdDf4admHyk8Uj81fbzz9PX0uXQnQmVVQTRyrePt8/a5dCZLdlY/KkRpl8np9blzIkJpVT4hHC1PqN/0uXIfN1dUPB8ZKUyn3/O4cR40U1E6HRgpTKfe87dwHS9LTzcbGClMpt7yt28cK0NMNRoYKkyn3/O3bxwtR0gzGhgrTajg9LhwHjJQRjIbGSxPquH1uXEfNlZGMxwaLU+r4va5ch86XUk1HRstUKvj97pyH0JrTjkfGyxPrOT4u3IeRHBSPCEbLE+s5Pm7cRw9Zkk1HhonQ5fN5adiGjVWMCUYFx4sW3qJYToWME0VFBITFRcaGhkSDxIvSA==",
        "2:CAgICQwQEhIOCwkKCgoJDBUzVXmCf3BLKhIMCwscOn6x2N7b0KJrKhYODTp70fLz6uv06bpVKRITXrzo8eGytu301oZCExtv1O7huml24PLiok0NIW/N48iOQ13b8eOoTgkaSoWbhFJAceb03ZdFCBUhLzYxKFue8PTOcDEJFRYZGyMykdL16LJDHQwVFRYaOWzB7/XEfyIRERUVGCZkvOLw5os/EhEWFBYfUprs896vUhUQFRoUHTeY1fftuFcoDxQZHBM7eMvw9c+TQTUvLiMaFGC+5/f1yqWHiYV2Qhgactvw+fnz7+3t5dFrFhhpyuPr6ebk4uPZvl8REkB3iIyIhoWEhX5rOA8MDxIUExAPDw8PDw4QEQ==",
        "3:BggLDQ4PDxAPEBAPDQsHKll9jIyMi4uLgGMyDAlKntjv7+7t7e3arFILDFOu4vb29/j5+em+WgkOLll8i4+63fj32pVECA4WIS0/Wavg+Oa1WCgLDxASF0CAzvb3wHkgEhIODxQqcdLr9OuTRhMSGQwNFDF+4+/y7LNzKxsYDA0RJF2txNjr4bhfLxMNDhATJUFTgs7l1pBEDQ4SFhkbHSBUuuDhrVILEi9TZl9GKk+14Oe8WgsWYLrdzpxJUrfh6L5bChRjxOvmw2lrx+jmtVUIEFey5vPjra3j8d6cRwcMP4fX+Pv29vr0y2wuBwsfQo3B5Orp4bqBMBYLCxAcQmaGkpCBXjkTDxAKCw0OExwnJRUMCAsQFA==",
        "4:CQgICAkLDAwLCAcHBwgJCQoQMmV8emAvDggJCgkKDh9gvdjPok0UCwsNCQ0XOIbr69CYQw4QEhMPEyFapPTqvW0vDBUYGBIZLn3A+eWrSSANFhobFCA+m9b42pUrGRQaGxsSK1i76fC5eS9DT0cwHhE6ec/u5ZxvX4yhiVAiFFCg3OvYgmuTyNq3ZiYdacXo5cd1a6rW4LdpKjN/2+/t3Le22evs0pFdRI3j8vXw7O/3+fXpsoNDhtXn7ero6/X59+6veSVGbnh7eXyYzOju04NCEBwpLi4tNGK14Oi/ZyEJDxYYGBkjVrTg57dfGgsQFhYXGCFRptbdqFcYDRAUFRUWHDZmgoVhMxAPEBMTExQVGBsbGA8LCQ==",
        "5:CAoOEhMSExMTExEOCwkJLl19iYmIiImIeVQpCgxQp9Xn5+Xl5eXNkEMJEGHF7Pn38O3u7taZRggSY8ft7tadgYGDdE0mChRkx+3ku1koKCwpHxcSFWTG69+xRhIVFxcWFxcUY8To4Lt5XGRSOx4XFhRiwubq3NPS2cGSQSERFWTE5/Lu5+jx7MhzNw4WYb7k6diur93t3ZxLDxZKjLKsik9fueLntlsTFipGX1c7IUWm2+vFZBYWQnuln3g6TKze7MhmFhNUqNzfwnV3yOnqvF0RDUue3/Trv7/r9eGgSwoILmnH8vr29vv0ymktBwgXNHOp1+Df1KZuKxQJCQ0XMFJ4goBzSygOCwwKCw4NDxARDwoHBwkMDg==",
        "6:CwsLDA8UGxsUDgoKCgoKDRY4XICLinxTLRELCwobOoG12+Ti16hvKRQLCTl90PT59fT37L1XJwwOWLbn9Om+v+vz14c/DBVozu/qyXR30erdmkoOGm7V8OGzV1SqxbyBPxAZbdLt5MOTh5+PcDwhEhZq0Orv5+De4cKNOh8VFGnP6vLu4eDt5r9mMxQUadDs7dugoNnq2JRJEhZr0e7kvlpbwOTkslkTF2vR7t+vSUy44enCYRMWac7u4LFKTbvj68ZjExFhxOvpx29xzuvpvV0QC1Gq5PTntrfq9OGjTQsINHXP9vv19frzym0wCAobO4G23uTi16x1LxcNDBEbOl6Diod5Ui4QDxINDxESFBcaFxALCQsRFg==",
        "7:CgwOEBEREhISEhEPDAoPNmd/iIiHh4iIgGk5EhVdt9vp6Obn6encuWIdGmrM6fLv7e7z9enJcSkZZsThzJmMpOHw5bZiHxhLiaCCSEx+3/DdmE0VFSEwMykZR47s89FyNAsUGBwcGx1or/Luvk8iDBQVFxgbJYbL9eGmMhYOFBQWFyAynt/2zoofDxEUFBUXKkyz7Pa0aRUPFRMUFRc6csbx8pZFEBAZExQVGEub1/HpeCUNExwTExUcXcHm7dVhFQ4WHhUUFiZx2+/grkoNDxgfFRMYPIrt9NGCNQkQGiERERxbp/f1wFcjCBIcJA4QHmeu8eGiMhYLEx8nCw4YRG2Sg1oYDgwSHyoKDA8UGBwXEAgKDRAfKw==",
        "8:EQ4KCg0RFxcSDgwLCwsMDxc3V3Z9fXhaORQMCwcaOXikw8jJxal7NBkLBTN0uNTV0tPW17twNAoJVbTY5d+npdrl27RUBg1m0Ofo1W1t1Orq0GIGD2rW7OjOWlvR6+3VZQcPYsjo7uCOjd7r479aBw5Mntvy8ODg8fPWikIPDj2A0PH09fb29ctiMxoOSJXY8vLh4fL01H5GIQ9fwubx56Wk5PLnvGMdEWvX7erTYWLW7u/UahISbtzv6M1WWdXu8dlpCxFp0+zu231+3vHu0GMIDlq55PPtx8fs9eWyUwkMPIDQ8vX09PbyymoyEQ0mToy52ODe0ap1LSEhEh4xT2yJlI54VDMYIS0bICYpLDA2LxoUFR4qMw==",
        "9:CQkJCg8VGRgUDgsKCgsJCxVFa4mQjH5VLxAKCgkfRJPG5erm2apvJREKC0mb2PP28/L27btMIAkUZcnq8OKws+vz1oM8Ch913PHftGFu2/Djp1ANJ33i8tGOOE7R7ei6WxAofODxz403TtDr6L5eECF01+zXplBg1evlu1sNGGTA5ObToafk7uK4WAsVRYnO7PDn5/Hz47hZDBUpTaHS6ubp8PTmulsQFi9VjKWoi53c7+i9XREXWKrOxqBRY9Tt6b9eDxNkxuroy3h+3/HmtVcMC1e25vXuwcLx9tyVRAgFNnzR9vv4+PvwwFUjBwUZPIrA5Onk16htJBELBgwaQmeJjol5UCoODA8HCw8RFRkbGRELCQsOEQ==",
        "0:CQ4UFRQTFRUTExQUEA04UW93e3t5eHh4c2dMNn+hytbc3djW2dnRvpV01eHv8fPz7+/y8vHr1MHo6OPJu7OysrS+y97c2O3gx4hjUFJUVWuQytvh7dixWSYOFBcWNWq/2eLq1a1VIwwTFxY2a7/Y4ejTrFQjDBQXFjZrvtff6NKrUyMMFBcXNmu+1t7n0qpTIgwUFxc2a77V3ufRqlMiDBQXFzdrvtXe6NKqUiIMExcXN2y/1t7o0qpTIgwTFxc3bcDW3unWsmI2IyotLUt7xdnf6uDNoYqChoeImbTa3t3l6vDs7O/s7fHz9PPh0bvQ6Onq6+fn6+vo4LyeZnaJiYiHhYWHh4V+ZVATGB4cGRYXFxYWFhUOCQ==",
        "1:BwgJDBIaHB0dGBIMCwsHCRE4Y46RkZBySxgNCggLG2Sr7O7u7cKCJRALCQ0fdL36+Pn81pUrEgwJDBhFdabK5vrVlCoSDQoMER0vR5HN+daVKxMPCgsNDQ8Xcbz42JcrFBAICgwNDhZwu/fWlSsUDwgJCwwOFW+69tWUKhMPBwkKCw0Vbrn01JMpEg4ICQoKDBRuuPPSkScRDQgJCgoMFG238tGRJxIPCAkKCg0Vbbfz0pEnFBMICQoKDRVuuPPSkSYUEwgICgoNFW659NKRJhEPBwgJCgwUbrn105ElDwsHCAkKDBRtuPbTkCUOCQcICQkLEUyQ27FyHw0JCAgJCQoNKE9/ZD8VDAoJCQkKCgoMERcTDwwMDA==",
        "2:DBAUFxcWFxgXFhUTDwwtSGp5gIOCgH+AempGKWeRxNfh5uPh3+Law41iudPy8vT39fT09vbx0rjP3+3PvLKysbHH3e/fztDa251vTFBSU3ur5ODWsrewaDQRFxwfTYve4NZGS0wxHxQgMkl1qeXezhcZGx4lMlR6pcPb686yFRwoQWOKr83k6+bPnncjPmWYv93j5N7Mr4FRLmCJvdbk6te5kG5NLx0So8Pm3cqximE2JB4oKCbH1eC6g0MnFhIeOGl3fc3X2JddKyYmKUFrr8DG0NvgspOAg4WHl7Lb3tvP3/Dp5+rq6ens7u7i2LjM493d4ODf3eHi3s7AaXWDgH9/f39+f4B/dW0WGBsaFxUXGBgWFhkYFg==",
        "3:DA4QFBcaGhoaFhIMCwofN1dve39+fnx3alAyHD9urNbo7Ozr6eTRqms5earl8fTx8fDv7+zkqHWZw/DZvaCen6K81ezDna/J3qtyODc5QHWs48yxoK+1dz8NERQZVZfczbZdaG5FLB0kKS1opODKriYpLz5eh5GWlrTP5LaKDxMgVZni6Onn6urloWgOEh9Vl97j5OHk5OGkbxcbIz9nlZmbmazD3LmWR0tOPDc6Ozs7X5DQyLegoJZcMhQVFhc9dsnNxcDFwopXKy0wM1yR1dPGuMvbvZ+DhoiKpsXn0bqbwOvp493f4ODm6+3CnG2b0+Tr6uvr6ujh05NdPFNuen9/f39+fHVpRysPERQXGRoZGRkXEgsJCA==",
        "5:EhQWFhUTEhAQEBANCwpTZXp7enh1dHRzcW1TPpm63+Lh3tva29nVzp51tNHy8e3p6Onr6OXfs4640+/ZxLGwsbSyrqmEZLnT5q99UE1NT05MSTkrudHhnGIyLSwxLScdFRC2z+W/nYB/g4p7ZkouGrPN6d/Wz9DT2NHCq3ZLqMLg393Z2tzg4uDap3x8kailoJqanaOzxdq6mz9FS0U+NzQ2O2KTzsKuVVVPNCARDw4PPXnHxrqioJNYLxQSEhQ/ecnJvrjCw4NVNDQ2Omeb1sy7t83gvaOQkpabu9boyq2ox+vp5N7h5Ofu7+i2i3ibx9zj4+Pk5eHUuXlFPFFtfIKBgICBe3BaNhkSFhsgISAdHBwYEgkHBg==",
        "6:EQ4LExYUDg4UEQ4KDA49W3+DhYaLiYKDgXpWOIWz5uLi5e/s3OPl3ah71un99PP3/Pjs9fv93sPl7O7KtKexsKS2zuzg0Onn3J1uSlFST2eQ0NLK5d/LgEUXGBoeOGKiqqng2sh+RhoeIiYrNUlPUtzf2aaId4eLg4aJi2RB2+bv2dLV5uXS2+Ljr4Lb5vDg29zo5dTc5u/Rtt3g3K+Uho+PhJu64tbF39vKgEokJyorToTU1Mnh28d8QhYYHCFGgNfYzOHez4taNz9ERWeY4NvN4uXgspaJmZ2Urszu383c6vfk3+Xy8N7r9fnfx5XB8uHd4Ozp1+Dm5bWKS2+Xj4uLkY+HiouHZUcZHSElJCAeHyQiHRYUFA==",
        "7:HyQqJiUmKCcjJCUmIh6KjI6NjpCNjI6Ni4mJiebk4uXl5N/g5+Th3t/g9fHs8O3m4eXy8vDt5+KWk5CKiYuEjKLL5urRujg6PDMxMzJIdLrl5K5/FBgcFhMRGEOTz+rVgDkSFhgUEREpadDk2aVPDBIUFhIRE0WN6+W+bi4DEhQVEhMcesHz1ps4FAMRExURIkOr5vS3bRcIBxASFRg7dcTp5Ic4CgcKEBIXKWW6297DVQ0HCQ0REx5Pmu/tyYU1BgsOEBAVK4nK89eaPRkIDQ8QDyJKuuzxtWoQCgoPEA8QOXjW9emDNwQIDhEREQ84dcfVtEwRBAoPERERDSZKd3pfIwUECg0ODg8KDhMWFA4IBgYICgsMDA==",
        "8:CQkKDA4OEBIVEg8LCw0WMFJmb3FwcXVuXT4mFj5uqsnX2djY29S9j1ctkLTf5+no5+jp6+bZmmWvzOrZxK6usbfM3uvAmsXT2qFxSElOVYu+6dK4y8/HcjcQFRogZ6zl18LAysdzPyIoLjJ4uOnTuafB2LKajJKXm7zX6LmOlrni4NzY297h5ufhnWOaveXk4Nvb3N7h4dyfarfK3MKqlJWXnLDG3r2dy8vBgFEwMzY6ZJja0sLPxa1dKgwSFRhKitrazs/KuXFEKS80N2mi4t3PyNLXr5WHi5CVtNPu2sW50ezn4+Di5efu8vLHoomr1OPp5+jp6ubezYlQSl52goWDgoOFf3VkPh8WGR4iIiAfHyAbFAsICA==",
        "9:AgQHCQgFCAkIBAUNDAkuVoWDh46HhIaKiX5RK32y7+vy/vHs8fn24KNw9vj59vj89/f8/Pnv6eX/+uy8pZ6YlpavyePt8v723oVYR0dGRGqb2ez0/PHRWx0FDxQUOHTS6vH368tZHQUPFBI3c9Dn7vXs145wbm5uboep1+bs9PDo1Nnu49/i6uvh5Onu7Onj5u7o5+vt6+Lj5nBwcG5tbm1tb4Sk0uDmKiYgEwwICgsLMGzH4OiFc1cpFBETFBQ3cs/m7dPGq2I/NDQzMliM1enw+/Tgn4aFfnt9mbre6/D++vLb4vnn4ur09erp6u7x8ubs/evl7Pf24su5iJOgnKGpnZiao6KVeWMRJTw+QEI9OTY6PDkiDg==",
        "0:CA8XHSEjISAhIBwUDQkbMk9gZ2hnZ2ZkXEouGThno8PPztDQzsq7nGEzbpzT4eLb3t/d39zPkV2TvOrfz7y/wcHP3eqwfJ/A37GGXl9hZIew47aIor/VklgmJScuXZfdtouhvM+ERA4MEBhMjNq2i6C7zYNDDg0QGEyM2bSKn7rMgkMODRAYTIvYtImfusyCQw4NEBhMi9iziZ+6zIJDDg0QGEyL2LOIn7rMgUINDBAXTIzYs4ifu86GShYWGSBTkNq0iJ+91ZllOTk9Qm2h3rSHncDkyKyRlZiascrns4KEr+He1MfKzMzU2tufalyOy9vf293e3dvSvXxHLkdncHJwb29vbmhbOyEPGCMnKCglJCUkIRkQCQ==",
        "1:CgkJEx4oLCwnGhANDhAPDRAmRGZyc2hJLBUVFxUUGUF+xdzeyY5VIh4jFxUZP3vC4+vboWQlJi8ZFhczZaPO4+CpaycqNhoVEh04XprE3KhrJyk1GhUPER85ea3UpWsnIigaFQ4MFipso9CkayYaGRoVDgwVKmuhz6NqJhkYGhUODBYqaqDNoWklGhsZFA4MFipqoMygaCYgJRkUDgsWKmqgy59oKCkzGBMNCxUqaqDLoGkrMkIYFA4LFSpqoMygaiw3SRkUDgwWKmuhzKBpKjRHHBYPDBYra6LOoWkmLT0cFg4MFilmmsSZYiEnNRoUDQsTJV2MsohVHCAsFBALCg8YNk5eRSoRFR0PDAkKDA8dJSYYDQkOEg==",
        "2:CQ8WGx8iIB8gHhkRDAkaLkhcZmpqaWliVDskEzVelLzO09XV0setfEkjYZDJ4ujk5ubl5de1dUJ+q+Dj2cjIys7f5NaRV4Kp0reVcW50g67O2plfZICad1U1NEFdmMTYmF44RVA6KBskPWWhy9eVWhshJyYuPmKGq8vYxoBFERknPl2Aqsja2cebWyoaMVSEq8va28yshFIsEzpglMDU2MeshWJDKBkPZZPM3c2ofFMtJSMpIx14o9LCm2VFMClBW3JaQoOq07KFU0VETnedu45khK7bzbeenJ6kvdHan2p9ptfa1czP0dLc4NqaY22SwdHV09bX1dXPv4NROEtiaWtra2trbGhfQCcVGyIjIyMhISMjIh4TCw==",
        "3:CAwSFhwjIyIhHhkRDQwRIz1WY2lpaGdhUzslFh9HfrTM0dHR0MWse0onQXW42uLb2Njc39S2eEdfmNrh0bStrbfP3dubY3Ol28SWW1NWZJjF46p1ZYy0kWIvLjVDfrXhrHhBW3VaQSwzPEmCtt6ocx4qOUJXc4GLkbPK0JBZDRMeQXe3y9XX4N3IgEULDxk+dbXH0dLZ2cyHThkgLDxafomPkqq/z5dkOk9jTkE7PkNIcaDTqX5nh6R3Ty0rLzdmnNewhYas0aR0RkNGT3ys3bGEfavd1sOqrK+yx9jiqXZcjMfY2tPV1tfb1sWHVCxblrK/wcDAwbqqiVEmFyxHVVxeXFtbWE8+JxUKDRIVFxoXFRUUEQsLDA==",
        "5:EBkkKCoqJyYnJyUgFw8qRGRyd3RycXJxbWRDJ0h5ttXg3Nvb29nSxYFIUojK4+XZ19bX1c/BgkxWj9Ph1Levq62rpppoP1OMz9CtdGBXWFhVTTUhUYvP07B3ZVxdWE8+KRhQis/dx52UkJCGc1ExG06HzebizMnJysO0l2A0RXe209bKysvP09HFhU8tT3uQkYWEh42owNKaZiM6VVlPPjw+RHGh0qV2NExlVDgVEREVSIXMqYBPdp+HWyYfHyVUjM6pf16Sy7qQWFFSW36m1ah6Vo/T3c+ysLK4x9XeoWtAdLfb4tja297e18WETiBIgLfN0NHR0cawjFQnEiQ9V2NnZmVkXlI9JBEJDRMXGx0bGRgXEwoIBg==",
        "6:DhASEhMUEhERDw4NDAsUJDhFTFBQT01NSkInEiBFdJSkqayrqKmkklQiPXGz0tza3t7b39/Wj1NVkNfn38rOzsrZ5/Gze2Ce5N++jY6MiajK7r6QX53h0ppLQDo7Vn67m3Zdmt3LjTYqJSc1UH1oT1uY3NiraGZkZGhudVAuXJfe6Ne2vb+7wcK7eUBcl97p3sfQ0czZ4uOgZVuW29vAl52dmLXR6reIW5faypRJQj8/b6XgupBbl9rJjTktKCtfm968lFqX3NGbT0ZCRXWq476VVZPd5MufoqSkvNXsuotEf8jj4tHa3tvk6OSmcCdXl8TRytDRzNDNwX5FFi5Oa3Zzc3Jxc3BoRSgMEhsqMDEsKissKiUcFA==",
        "7:KzZCQUFDQEBBQj82IxVcanp5eXp4eHp5dGpCIKa60M/Pz8/Q0s7Hu3AwssXb2tjX2eHv7+LCbSiNna+sqqeuxe3146tYFzpGUk5LSl2P4unOgj0LEhgeGRcaRIfl3K9SIwgKDxMPDxVkrO/GhScRCwkMEAwVK4nL8adaEQkNCAsODCNNrN3fgDMKCg8JDA8PN3nJ37tbGAgMEQgLEBxUqNzQhTgJCAwQBwoTNX/c5bRHHQcKDA8GDiBgp+7NiSQQCAoNDgcUM4/N8aZaDwsLCw0OCiFNteLifDMHCw8ODg8LL2jK27dUGQcNEQ8PEAw3d8O5dysGCQ8SDxARCSNIb2Q6FQQIDQ4NDg8HFSg2LBMIBAgLDAsMDQ==",
        "8:CQsPGyQrKikoJR8WEAwPHzZXbHp4d3ZvYEcqFRo+ca7U6ubk5di+kFMlOm6x1eTm3tzg5N7KilRVktri1bywrbPL3umuemuj4MudYFNQV4i65rqNbKPdwIY7MjA3b6niuY5jm9jCjUlBQUl4q9+zhUSDzdfGpZ6fprnK15xnOHjH4ube19fd4t/Sj1dHhc7e3tPKx83W2tWXY2GY1smvj4SCh6C71ah8eqnYq3M1MC80X5PStpR8q9mpbi4rLDBcktW5l3ur3buMWFZXXH2o37yWbqPh3s+6tre9ytnpt4lNgsPa4uDb29/g286VZSJQjLbM1c7M0Mi3ll4yFChDWmVpZmRlYVdGLBcLDhMcHx8eHRsaFxELBw==",
        "9:BQQCBgcIBwoPCQgOCwcGEypWaGtqbG9raGVCJQcrZs76///////45pVRDE2k4/Pl4+Lg6O7vvpMRZczu4Leyrq2+1PDYwBVozOW3YVRQU22Z39bFFmnM4KI3JiEoRXzZ18kWas3dmCMQDBYxbNjazBZpzObHhoWGh5m359vKE2LC4+DJ0tXR3Obw28cKT6XGz8jMzczV4e7YxAc1boOFfXx9fpCu4dXEBxcrLB8MBwgNKmbO0scMOnB1ViMdHCA9ddTUxxFWqLWQTkpISWST3dbFFWXF4c+ipqahtc/u2cIUYsPq6dHY2NHg6+zHphFYseDx7vb27vj24Kp+DStScn59fn9/gX91VDgKDRMpMTEvMDQyLy4bCg==",
    };

    private static readonly string[] SmallEncoded =
    {
        "0:CQ4XHiIjIiEfHhwXDwshLEFOU1VVVFJQTUErIVFol7C0t7m6uLWwlmVNfJXH3NXR0NDP0tnCjnSjs9PTtKGalpamx8enl7rE18WPbmJeYH63yretwsjTs2Y5KiYtWanLvbbGydGoUR0NChRHo8vAu8XIzqZPHAwKFUikzMG7xMfOpU8bDAoVSKTMwbvFyM6mThsMChVIpMzBu8XJz6dQHQ8NF0qlzcK8xsnRqVQiExIcTqjPwrzEyta6dExBQElyudTDusDL4NiynZiZn7XZ3sO1o7TX4dLKyMnN1eHUrppth7zV1NPS0tPT07iDaUFYh6CkpaWkpKGcglU+Hic3QEJCQD8/Pj0zIhkNDhARERAODQwMDQwIBg==",
        "1:CAgJCgsPFBUSDgoICQoHCQ0aMD9GRDoqFAoLDAYLFDh5nqmhh18pDg8PBgwXRpjK3Ne6hjkUGBoHDBhBir7e5dKcQxwnLAgMFTBcjL3U0aJFHiwzCQsOFyRPlr/LokYeKi8ICQoLDTaHuMiiRh0lKQcHCQoLNIW2x6JGGh4gBgcICgozhLXGoUUYGhsICAkKCjOEtcagRBcaGwgJCgoLM4S1xqBFGR4gCAkKCwszhLXGoEUcJywHCAoLCzOEtcWgRB4tNQcICQsMNIW1xZ9DHzI7BwgJCgwzgK6+mUAdMDoICAoKCy1xmqeGNhcoMQgICgoJIVJveGAnER4kBwgJCggRJC8yKBEJEBMHBwkJBwkNDg0KBgYJCw==",
        "2:DA0QEhMWGhsYFA8NDg8QHTVFS05NTUxJQjMbEBk7f6q7v7SxtbOpfzYRPma15fb25eDo6eO1XTF/nNb2+/Tg2uTr8dORcJCq3enOuKmmrsbr5K+Uco/Kv21DQEJIeNLnt59PZ5aFNBAcLUN70+SulCcwQj0iIjxnos/v3JZzExwuPUtddZ7X7N60b0wSKlmFrsPG0OHRnmw5HzBOirzj6Mq4sJVnPx8PbInB4uvLg1VDODYvIRubsuDu261mOSYtTFhRTrvK6OGzj3RjW3KovK6nw9Dr47eel5WWr+Dt2M2yxer159rQz9jk9e3PwIWWtsbFwLe4wcXEt52QPENQVVFPTk9RUU5JQj4YGh0dFxYZGhkWExMUFQ==",
        "3:BQUFBwsPFBEGAAECAwM9PkJFR0hJR0I5LR0LAqyxu8C/u7Ozu6uEVB0B5Ojw6NDBucXl4bp9KgDl4+C8ellafcDc0JgzANHGsYQ9HSNTrt3gqjsDp5FlPRoME0mv5eq0QQhyZ1BDQEBCbcLn3aU9CjNIc5awubC/5uO5fS8IFTNvoMjXz9bt4rZ6LggaJ0JiiJyestjj1Z07ClFLP0NWZGuIu9rjsUMLup5kQTMwNVeWxeK4RQzw0phuVUlMaaHK5rpGDPDp2su6sK682uvwuEQK0dru8ube2d7s5syRNQeRp9Pk2tLMzti8e0UZAllul6ihm5eXm34/GQsELDA5PDo5OTg3LBgNCwsVEQoGBggKCQUEBAcMDg==",
        "5:CQoOEBAREREQDw4MCgkcJz1ISUhGRkZEQTMZDEFfm7m6t7GusK6nfzcSYofQ9PPu5+Xo5t6qSRl/ntv38+7p6O3s57RRII2o3urMubOxtbayjEEcjafazH1QREBCQj8yGQyMpdjKe05CPkE+NicRBomk2uXGs6ursaqXbCoJepfS7OXe2Nnj4NGfSh9cfsLg18/Hytfg5L9wSFBspLekmJKVo73i0YhkVWB3cE06OD1JeMvUlHNudH9qNx0cIjJoxdSWd5ymuqRkQj1GXozQ0ZBwpLXZ0Jt9eICXttzKfVeFodvt2s/N0dvk675cLFx2rMbGxcPExsO8jzwSKTVNW15eW1lYVVA8GwoPFB4lKysnJCEeGhMKBg==",
        "6:CwsNDxIVGhsZFhMREA8WHCg0QklLS0pIRDstJi49XHyfrqqpq6qlj2hUSF+OtdPc0MzR1NbAkHhce7nZ3NO7s7rE0seij2eHx9rBpIN0eIqqrpmOb43IzqJ1STIxRnGCeXVwjcjRqH9WQUFOaHBmYXCOyd3KtZ2TmJ2jlHFgb47K5+XbyMPL0NG6iHBvjcjl49W8s7rDz8Kdi2+MxtrHro1/gpW2vKedbovFzqd9UT0/XZaupaBui8bOpHlPOz5enLWpo2mJyN/NtpqOkqfM0LOkWHm63ubgzcfO1+PVrJc2U4+50tnOy9HT0buQexsyXoGbpqGgpKKahmRTEBgoND1BQUFBPzs0KSQKCgwMDA0PEA4MCgoLDA==",
        "7:CBIlMDIyLy8wMTIvJiIrOFJfYGBeXl5fX1hHP3GFrL++vbu7u7u5qYp6hJe+z8zKys/X3+TNmoBkcIiSjIqLmbPL38R4UkFHVFhTVFp3qcratVwvGh0hIyEpOWm53dWiRRYHBwkKCh5Dfcrdt30vCAYHCgwOM3my3MyCRhoEBQcKEBhIodPetloiDQIFBwoVJ1674c+ZQA8HAwUHDCJLhdHerXApBgUFBQkPOIO848x4PBcFBwcGDh1RrNzhs1QcDQYJCwcWNG7F5MqTPhELCg0PCB5KhM7XomgqCgoMDxEJJmCVxLVoMxYJCgwODwghU3mTfjoUCwgJCwwNBQ8jMTgwGAsJCAgJCgoEBgsMCwoIBwcHBwgICA==",
        "8:BAkVHykwMzIuJBUMCgkQGi9BU11gX1tNNyMSCSk8Yoamt7q5tJ97USEJU2mUtMrW19bUxq6BQCCOoMPOwLi1trrD0bVwTrrH39Oih4GDjara0Y5t193oxHFDPD9Ne8vVmXvL1ObHd0xHSliDzdOVdpSr2d22oqGlrsHfyoBbg57V5tLJys3T3OjHeVKWrNnjzcHBw8fS5suAWrXD3taqlJKTl6/a0ZJy3+Hmvms/OzxDcMTZr5r08e2+ZTczNDtrw967qfPz89WZeXh6f57Y4LWgyNLm4L+urq+xwNzQnYNzj8bf2dbU09LR0LBxUTlVja2ztbOxr6ibe0YrGSU8SUxNSklJRj8xHRIJDBMYGRkVFRYVEQwIBg==",
        "9:BAQFChIWFhYWFxsaFBEFDR0vQ01NTU1OUE5KSAYdS3ikuru7vLu5t7a1Byx2sNrq4dvY1tTZ5esJOp3V5Ny+qqGeobXY6gtDsurqzZV0aWhylM3qDUW27eu+ZjYwNkh3xOsNRLTr68JxSEZNXojM7Qk/rOXq27mpq6+2x+PxBzKGucvRy8jJy87X6PEHG0JojqOnpqGhprnc7QgVMVF4h352bm94mM/qCCFTdYl9UTgwNERzwegJLXafp5JgRD9FVYDI6wk6ms7UyKqbnKCpvuHzCDaQx9rd0czO0NLY4ugFIVeKudLW19XT0M7MywQSLVOCnqappqOfmpOPAwgTIjZAQkJBPz88NzUCBAYKDxEQDw4ODw0KCA==",
        "0:CQkKDBEWGxwZFA4LCgoLDhMdLjtFR0I2JBgQDRAXIz1kgZSYjnRNLxwSGyc/Y5OxvcC6oXZQLhwoO2OOus3Gxs7BoXdDKTNLfabHyaqnwMSzjVI0PFeOssW1gXumurubXT0+W5W3xKxvaZu4v6NiQT9cl7nCqWtmmbjBpWRDP1yXucGnamWZt8GlZEM/XZe4wKZqZZi2v6RjQ0FemLi/pWllmLa+omNDRGCXtr6la2ibt7ueXz9FXpGxvat5d6a7uJdYOEFXhKjCwKOjwsezi00uO0xwlbzNyMnSxKFzPCExO09wnrrExLqfcUoqGSYrM0tyjJuZiGtDKRwVGx0gKjtGTUo+MCAXFRUUFRYZHiEkIRgSDQ0SFA==",
        "1:BwgJCg0RFRUTEAwJCQkLDhMdKjQ7Oi8iFA4QEREZKEFje4mCZkckFhwfGCM6XpGzxLmRZDEeKjAfK0Rtpszh1ap2OyY3QB8oOVyQu9/bsXw9KD1HHSEoP2aVzNWvfD0nPUgdHR4uTX2/z6x7PCc7Rh4eHCpGdrnKqHk8JztGIB8eKkZ1t8eldzwpPkkgHx8sR3Szw6J1PSxBTB4fHy1IdLLBoHQ+LUJNHR4gL0p2ssChd0EwQ0wcHyQ2VH+1w6iBTTlFSyAlL0VnjrvJt5NdQ0VHJCo3T3KVu8i/n2hJQ0AmKzZKaIentLCVY0U7NiMmKzdKYHeDgm9LNS0qHBsaHiUuODw7MyQcHBwYFhISFBYZGRcVERAUFQ==",
        "2:BgcHCg4SFRcXFBANCwsJCw8aKzhBREI6LB8UDg4UHjhgfpGYkoBgQSMVGiU7X5GywcbCr41nPSgoOl6JutHQ0dbNt5BZPi5DbZW9xq+su8LAomhKM0dujqWhg4KftMCoa00vPFVodXNlc563vaFjRCowO0lYZGuGtMW5lVk7KSwyQ155k6/QzquATTQpLjhReZmzxc+7iF4+Liw1SGiWs769r41WNzIwNEJfg62/uaWEYDcoMjc6TXObw868nXJSPjlDSTtSfqfM1saxl4R1ZldPPFKApsXOw7u2rqKIYEs7TnOSqrSxr62klHZKNTNAW3B/hoWFhX5uVDAdHiMuNjs9Ozs7ODAmFxAREhUXFxYUExMSDwwKCQ==",
        "3:CwwPERITExEPDQwLCgkMERwoNDw/PjozKh4QCQ0bNlZ5kJqblIFkQhwJEiVKdqjG0dPNto9fJgkbMVyLu9XX2t7Qsn0zDiI4Youww8PM3da5hTkTIzVaeZKenKrHyKt8ORcfLkxmfoySo8C/oHM3GRggMEZhgKG80cacazIVExYcLUhvocXc0qdyMxQUFhsnPV+NttjXsXw4FhohLjpGWHCVx9S7iT8ZHzBTaXFvYXeyy8GTQxsgPXebqpxxd63FvpJBGCFCg7DIwJeWvcm5iz0WHThwoMnUw8LQx6d2NhYXKlB+s9La2dGye00oFRQeNFWAnaurnHxKKx4YEhYfLT9KT0xCMh4XGx0REhYaICMjHhYPCQ0ZHw==",
        "4:DQwKCw0PEhUXFhIPDQsODgwQGSMuMzMsHxYQDRISEBowSGRxbVs7JBYPFRYXJkVnjZ2VfFExHhQVGB41XYSst6aHXDslGhUbKUZ0m77CqopjRCwhFB82W4+zxcCmiWlMMyYUJENupsHCvrSceFU0IxkuV4S0xLW1w7WKXzQfJD1wm77Dqa3MxppqNhwvTYewyMeustTRqXg9HzlZmMDS1MbJ3tm5iUssN1aUu8vRztPh2r2PUDEsRXeXo620w9nTs4VKLSIxTV5lcYOhy82neEAlGiArMjRCWoK7wplrOSAXGiAjJDBIbqOqgls0IBQWGhwcJTVReX5gRSwgEhMVFhYZIS0+QDIoIR4REhMTEhQXGyEgGhgcHg==",
        "5:CQsOEhYZGhoZFxYSDAkNFCMxPkVHR0ZBOSsXDBMmS22MnJ+gnpN/XCsTHTdsmcDRzMjFtp5zNRcoRoOz2ODKu7KjjmgyFyxLirna06SEdWZYQycZLEyKt9TEiGBNPTAmHxwrSYa11c6ig3FaPywjHidFgLDV3su9tJpwTC4fJ0B0ocfX0dHVxJxvPCIoO2KGqLSqssvMtopJKCc3VnOLjXmFscS/mVIvJzhZc4V/X2mcuL6bUCsrPF9+mJNwdJ+2uJNHIS9AY4mzu6OjvMGyhj8bMT5agLPKxMXNwZ9xNRcxN0RikrHAxL6jdU0rGistL0NmgpWYi3FJLyEbISAeJDI9REQ9MiMbGhocGhUVFxkZGBUSEBIXGQ==",
        "6:CwsLDBAVGx4dGhURDAoPEBIbLDpDR0U+MCIUDRYZHzllg5OZloVnRyQTHSY4Xpm7xcnHtJJnMxokNVmHwdjMxsa3mnA6HilBcaPV2rKgpZ2HZTYeLUmBsdnRmH+HgnBVMR8vTIe329amjpCDZ0suHzRPh7fe5Mq8uqR7VTIhOFKHtNvl083SwJhsPCQ+Voew1Na1scnJsIVGJ0VbiK/OxI6Js8S8lE4rRlyJrcq3c22jvr+aUSxCV4Knx7h5c6W+vZdOKThLcprEwpaSucW4jUYiLj5dhrnItLTHwqV3OhsmMERqosDFx8evf1MrFx4jLkp3lKGjmn9TNCEXFhgdKj5LUVFKPSgdGhkSExUaISYpJyIbEhEXGg==",
        "7:CQoNEBITExMTEhIQCwkNFCMxPkRERENAOSwZDxQnTXCPoKKioJaEYzIaGDJmkrfKy87SybGFQyIbN2+ZtcPAyt7cxJRLJxcqUW19h4yjzte9jEUhExsqNTtIXIS/zKp5NxUQEhUaIDZcjMXJmWUrDg8PEBQcO3ChzsWHUiUODg8PFR5FiLfRuXE+IBEODxEYJVKfx8umWSscFRISExwvYK/Ovo9DHRsaFRUVIz5xvNGvezUWHiIZGRgrUYXIz5tkKRQlLSAeHDNkl8zFgksfFi46ISEgOWyZwbFqOBoaNkQbGxovXIGejU8nFRs6SRMTEiJBWWteMRcQHDlIEREPFCErMiwZDw4ZMT0REA0OEBMVEw0LDRgsNw==",
        "8:DAwLDBAWHSAdGBENDQ0MDhIcLDlDR0Q5KBoRDQ0TIDxng5CWk35XNhsODhs3YZq6wMTHsoVWJw8PJlOGv9PCwtTNrns2ExMvZ53R1KejydPDkkAXGDVuotHKjYe4zcSWQhgbNWmczs6cmMTRwZA+FR81YZTO38bG39q6gzcRIjZcj87o3+Ht3bF5NBElOF+Pyt/Pz+HWrnk1Eyk9ZpPFyaCdwcizgzkVKT9sl8G3enWovreNPhcnPm2Zw7p9eKvAt4w+FyM5ZZTGyqCew8u1hToUIDFUgrvQw8TUyaNwMBEfKT9lm7zHy8iteUsjDxogK0RriJmdkndMLBkQFBYbJTVDTk5ENiMYFBIREhMWHCIpKB4WDw4REw==",
        "9:BgcHCQoOExQSDwwKCgoHChEaJzI9PzcsHhQPDQgRIjtdeI2OfGE/JxkSFSVGapOwwMGylGlGKx0oQXKdwNLR0tXCmW9DLTVRi7TMzba41NO0iVM4O1mVucS1jJHF1sKZXT87Wpm6vql6g8PbyqFhQTRUlLjAsImTzd/LoWFBM1CKsMHArbfg6M6hYUEySHObwdTU3e/r0KJiQTNAWn6rx9Hd7ejOoWJCPEJOZ4yjrMHf48qfYUJIT11xjJqcr9XbwZZaPEpXb4SUnZ+z2dq2h00wRlZ3kaa0vMrf0J9vQSo9TWyNscbMzManbUYzKTA6TWeImp2YiWs/KSkpGx4mMT9FRUE4LB4bJSkPEBIVGRoYFA4MDBQjKg==",
    };

    private static readonly float[][][] Templates = Build(Encoded);

    private static readonly float[][][] SmallTemplates = Build(Encoded.Concat(SmallEncoded).ToArray());

    private static float[][][] Build(string[] encoded) => Enumerable.Range(0, 10)
        .Select(d => encoded.Where(e => e[0] == (char)('0' + d)).Select(e => Decode(e[2..])).ToArray())
        .ToArray();

    private static float[] Decode(string text)
    {
        var bytes = Convert.FromBase64String(text);
        if (bytes.Length != Width * Height)
            throw new InvalidOperationException("A mining digit template has the wrong size.");
        return bytes.Select(b => b / 255f).ToArray();
    }

    public static (char Digit, double Score) Match(float[] patch, double tieMargin, out double[] allScores, bool small = false)
    {
        var templates = small ? SmallTemplates : Templates;
        var scores = new double[10];
        allScores = scores;
        var closest = new float[10][];
        for (var d = 0; d < 10; d++)
        {
            scores[d] = double.NegativeInfinity;
            foreach (var template in templates[d])
            {
                var score = ShiftedCorrelation(patch, template);
                if (score > scores[d])
                {
                    scores[d] = score;
                    closest[d] = template;
                }
            }
        }

        var order = Enumerable.Range(0, 10).OrderByDescending(d => scores[d]).ThenBy(d => d).ToArray();
        var best = order[0];
        var second = order[1];
        if (scores[best] - scores[second] < tieMargin && PrefersSecond(patch, closest[best], closest[second]))
            (best, second) = (second, best);
        return ((char)('0' + best), scores[best]);
    }

    public static double BestScore(float[] patch, bool small = false)
    {
        var best = double.NegativeInfinity;
        foreach (var digit in small ? SmallTemplates : Templates)
            foreach (var template in digit)
                best = Math.Max(best, ShiftedCorrelation(patch, template));
        return best;
    }

    private static double Correlation(float[] a, float[] b, int x0a, int y0a, int x0b, int y0b, int w, int h)
    {
        double ma = 0, mb = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                ma += a[(y0a + y) * Width + x0a + x];
                mb += b[(y0b + y) * Width + x0b + x];
            }

        var n = (double)(w * h);
        ma /= n;
        mb /= n;
        double ab = 0, aa = 0, bb = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var da = a[(y0a + y) * Width + x0a + x] - ma;
                var db = b[(y0b + y) * Width + x0b + x] - mb;
                ab += da * db;
                aa += da * da;
                bb += db * db;
            }

        return ab / Math.Sqrt(aa * bb + 1e-9);
    }

    private static double ShiftedCorrelation(float[] a, float[] b)
    {
        var best = -1.0;
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
                best = Math.Max(best, Correlation(a, b, Math.Max(0, dx), Math.Max(0, dy), Math.Max(0, -dx), Math.Max(0, -dy), Width - Math.Abs(dx), Height - Math.Abs(dy)));
        return best;
    }

    private static float[] Shifted(float[] t, int dx, int dy)
    {
        var q = new float[t.Length];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                int sx = x - dx, sy = y - dy;
                if (sx >= 0 && sx < Width && sy >= 0 && sy < Height)
                    q[y * Width + x] = t[sy * Width + sx];
            }

        return q;
    }

    private static float[] BestShift(float[] p, float[] t)
    {
        float[]? best = null;
        var bestScore = double.NegativeInfinity;
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                var q = Shifted(t, dx, dy);
                var score = Correlation(p, q, 0, 0, 0, 0, Width, Height);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = q;
                }
            }

        return best!;
    }

    private static double[] Standardised(float[] v)
    {
        var mean = v.Average(x => (double)x);
        var sd = Math.Sqrt(v.Average(x => (x - mean) * (x - mean)));
        return v.Select(x => (x - mean) / (sd + 1e-6)).ToArray();
    }

    private static bool PrefersSecond(float[] patch, float[] a, float[] b)
    {
        var weights = new double[a.Length];
        var total = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            weights[i] = Math.Abs(a[i] - b[i]);
            total += weights[i];
        }

        var p = Standardised(patch);
        double Error(float[] t)
        {
            var q = Standardised(BestShift(patch, t));
            var e = 0.0;
            for (var i = 0; i < q.Length; i++)
                e += weights[i] / (total + 1e-9) * (p[i] - q[i]) * (p[i] - q[i]);
            return e;
        }

        return Error(a) > Error(b);
    }
}
